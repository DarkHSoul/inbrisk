namespace Inbrisk.Platform.Windows.Native;

/// <summary>Read-only capability probes for `inbrisk doctor`.</summary>
public static class PlatformProbes
{
    /// <summary>True when this thread runs with Per-Monitor-V2 awareness —
    /// the manifest normally sets it before any DPI-sensitive call.</summary>
    public static bool PerMonitorV2Aware() =>
        NativeMethods.AreDpiAwarenessContextsEqual(
            NativeMethods.GetThreadDpiAwarenessContext(),
            NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
}
