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
            var outStream = Console.OpenStandardOutput();
            if (outStream != Stream.Null)
            {
                Console.SetOut(new StreamWriter(outStream, new System.Text.UTF8Encoding(false)) { AutoFlush = true });
            }
            var errStream = Console.OpenStandardError();
            if (errStream != Stream.Null)
            {
                Console.SetError(new StreamWriter(errStream, new System.Text.UTF8Encoding(false)) { AutoFlush = true });
            }
        }
        catch { }
        return attached;
    }

    /// <summary>Detach so the parent shell prompt returns cleanly.</summary>
    public static void Release()
    {
        try { FreeConsole(); } catch { }
    }
}
