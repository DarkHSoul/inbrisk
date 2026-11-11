using System.IO;
using System.Runtime.InteropServices;

namespace Inbrisk.Cli.Ui;

/// <summary>
/// inbrisk is a WinExe: it has no console unless one is attached. CLI and
/// MCP modes call Ensure() so terminal output works when launched from a
/// console; GUI mode uses HasConsole to decide between the window and
/// usage text. Stdout/stderr stay attached to redirected pipes when the
/// caller redirected them (MCP stdio is unaffected).
/// </summary>
public static class ConsoleAttach
{
    private const uint AttachParentProcess = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(IntPtr hFile);

    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;
    private static readonly IntPtr InvalidHandleValue = new(-1);
    private const uint FileTypeUnknown = 0;

    public static bool HasConsole => GetConsoleWindow() != IntPtr.Zero;

    /// <summary>Attach to the parent console if we don't own one. Returns
    /// true when a console is usable for interactive I/O.</summary>
    public static bool Ensure()
    {
        var attached = HasConsole;
        if (!attached)
            attached = AttachConsole(AttachParentProcess);

        try
        {
            try { Console.OutputEncoding = new System.Text.UTF8Encoding(false); } catch { }

            var outStream = TryOpenStandardStream(StdOutputHandle, Console.OpenStandardOutput);
            if (outStream != null && outStream != Stream.Null)
            {
                Console.SetOut(new StreamWriter(outStream, new System.Text.UTF8Encoding(false)) { AutoFlush = true });
            }
            var errStream = TryOpenStandardStream(StdErrorHandle, Console.OpenStandardError);
            if (errStream != null && errStream != Stream.Null)
            {
                Console.SetError(new StreamWriter(errStream, new System.Text.UTF8Encoding(false)) { AutoFlush = true });
            }
        }
        catch { }
        return attached;
    }

    private static Stream? TryOpenStandardStream(int stdHandle, Func<Stream> open)
    {
        try
        {
            var handle = GetStdHandle(stdHandle);
            if (handle == IntPtr.Zero || handle == InvalidHandleValue || GetFileType(handle) == FileTypeUnknown)
            {
                return null;
            }
            return open();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Detach so the parent shell prompt returns cleanly.</summary>
    public static void Release()
    {
        try { FreeConsole(); } catch { }
    }
}
