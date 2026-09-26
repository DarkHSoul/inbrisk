using System.Runtime.InteropServices;
using System.Text;
using Inbrisk.Core;
using Inbrisk.Platform.Windows.Native;

namespace Inbrisk.Platform.Windows.Input;

/// <summary>Win32 clipboard without a WinForms dependency.</summary>
public sealed class ClipboardService : IClipboardService
{
    public string? ReadText()
    {
        if (!OpenWithRetry()) return null;
        try
        {
            var h = NativeMethods.GetClipboardData(NativeMethods.CF_UNICODETEXT);
            if (h == IntPtr.Zero) return null;
            var p = NativeMethods.GlobalLock(h);
            if (p == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUni(p); }
            finally { NativeMethods.GlobalUnlock(h); }
        }
        finally { NativeMethods.CloseClipboard(); }
    }

    public void WriteText(string text)
    {
        if (!OpenWithRetry())
            throw new InbriskException(ErrorCode.Internal, "OpenClipboard failed");
        try
        {
            NativeMethods.EmptyClipboard();
            var bytes = (text.Length + 1) * 2;
            var h = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE, (UIntPtr)bytes);
            var p = NativeMethods.GlobalLock(h);
            Marshal.Copy(Encoding.Unicode.GetBytes(text + "\0"), 0, p, bytes);
            NativeMethods.GlobalUnlock(h);
            NativeMethods.SetClipboardData(NativeMethods.CF_UNICODETEXT, h);
            // ownership transfers to the clipboard — do not GlobalFree
        }
        finally { NativeMethods.CloseClipboard(); }
    }

    public void Clear()
    {
        if (!OpenWithRetry()) return;
        try { NativeMethods.EmptyClipboard(); }
        finally { NativeMethods.CloseClipboard(); }
    }

    private static bool OpenWithRetry()
    {
        for (var i = 0; i < 10; i++)
        {
            if (NativeMethods.OpenClipboard(IntPtr.Zero)) return true;
            Thread.Sleep(50);
        }
        return false;
    }
}
