namespace Inbrisk.Platform.Windows.Native;

internal static class Dpi
{
    private static int _done;

    /// <summary>
    /// Put this process into Per-Monitor-V2 mode before any DPI-sensitive call.
    /// No-op if a manifest already set awareness.
    /// </summary>
    internal static void EnsurePerMonitorV2()
    {
        if (Interlocked.Exchange(ref _done, 1) != 0) return;
        NativeMethods.SetProcessDpiAwarenessContext(
            NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    }
}
