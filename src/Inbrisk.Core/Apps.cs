namespace Inbrisk.Core;

/// <summary>How the application was located/started — reported back so the
/// caller knows which resolution mechanism produced the launch.</summary>
public enum LaunchMethod
{
    /// <summary>A running user-facing window was reused (no spawn).</summary>
    ExistingInstance,
    /// <summary>Start Menu shortcut (*.lnk) matched by name.</summary>
    StartMenu,
    /// <summary>HKLM/HKCU App Paths registry entry.</summary>
    AppPath,
    /// <summary>Packaged/Store app activated by AUMID.</summary>
    Aumid,
    /// <summary>Executable resolved by name on PATH / install dirs.</summary>
    Executable,
    /// <summary>Caller supplied an explicit file path.</summary>
    ExplicitPath,
    /// <summary>Registered URI/protocol handler.</summary>
    Protocol,
}

/// <summary>What "ready" means for a launch.</summary>
public enum LaunchReadiness
{
    /// <summary>Wait until a usable top-level window exists (default).</summary>
    Window,
    /// <summary>Wait only until the process exists.</summary>
    Process,
    /// <summary>Return immediately after the spawn attempt.</summary>
    None,
}

/// <summary>One resolved way to start the app — the winner, or a candidate
/// in an AmbiguousApplication result.</summary>
public sealed record AppCandidate(
    string Name, LaunchMethod Method, string Identifier, int Score);

/// <summary>Launch request. Exactly one of App/Executable/Path/Aumid/Uri
/// identifies the target; `app` is the friendly name resolved through
/// Windows' application registration.</summary>
public sealed record LaunchSpec(
    /// <summary>Friendly name ("Spotify", "Notepad", "Calculator").</summary>
    string? App = null,
    /// <summary>Executable name ("notepad.exe") or name on PATH.</summary>
    string? Executable = null,
    /// <summary>Explicit file path to an executable.</summary>
    string? Path = null,
    /// <summary>Packaged-app AUMID (PackageFamilyName!AppId).</summary>
    string? Aumid = null,
    /// <summary>URI with a registered protocol handler ("spotify:").</summary>
    string? Uri = null,
    /// <summary>Structured arguments for executable launches — never a
    /// command line; each element is passed as one verbatim argument.</summary>
    IReadOnlyList<string>? Arguments = null,
    /// <summary>Force a new instance instead of reusing a running window.</summary>
    bool NewInstance = false,
    /// <summary>window|process|none — how far readiness waits.</summary>
    string WaitFor = "window",
    int TimeoutMs = 25000,
    /// <summary>Optional remote debugging port (e.g. 9222 for Chrome DevTools Protocol).</summary>
    int? DebugPort = null);

/// <summary>Outcome of a launch attempt.</summary>
public sealed record LaunchResult(
    bool Success,
    LaunchMethod? Method,
    /// <summary>Resolved friendly/app name.</summary>
    string? ResolvedName,
    /// <summary>Path/AUMID/URI the launch used (debug — MCP may omit).</summary>
    string? ResolvedIdentifier,
    int? Pid,
    long? Hwnd,
    string? WindowTitle,
    /// <summary>AlreadyRunning | Ready | ProcessStarted | Started | TimedOut.</summary>
    string LaunchState,
    long LaunchMs,
    long ReadyMs,
    /// <summary>Machine-readable failure: Malformed | AmbiguousApplication |
    /// TargetNotFound | PolicyDenied | Timeout | Cancelled | Failed.</summary>
    string? Error,
    string? ErrorDetail = null,
    IReadOnlyList<AppCandidate>? Candidates = null);

/// <summary>One entry in the launchable-apps catalog — the name plus the
/// exact computer_launch argument that opens it.</summary>
public sealed record AppInfo(
    string Name,
    LaunchMethod Method,
    /// <summary>Ready-to-use launch argument, e.g. app:"Spotify",
    /// executable:"notepad.exe", aumid:"Microsoft.X!App".</summary>
    string Launch,
    /// <summary>"installed" — user/OEM-installed apps (Start Menu, App
    /// Paths outside Windows, third-party packages). "system" — Windows
    /// inbox components (Microsoft.* packages, App Paths into %WINDIR%).</summary>
    string Kind);

/// <summary>Windows application resolution + launch. Implemented by the
/// platform layer; the MCP surface never touches Process.Start or the
/// registry directly.</summary>
public interface IAppService
{
    /// <summary>Resolve + launch + bounded readiness wait. Cancellation
    /// aborts the wait; the spawned process is left alone (no kill on
    /// cancel — launch is not transactional).</summary>
    LaunchResult Launch(LaunchSpec spec, CancellationToken ct = default);

    /// <summary>Resolve only — the candidate list a friendly name maps to.
    /// Used by tests and diagnostics.</summary>
    IReadOnlyList<AppCandidate> ResolveCandidates(string app);

    /// <summary>Every registered, launchable application on the machine —
    /// Start Menu shortcuts, App Paths entries and packaged apps, deduped
    /// by normalized name. One entry per app, alphabetically sorted.</summary>
    IReadOnlyList<AppInfo> ListApps();

    /// <summary>Last-resort filesystem scan for executables whose name
    /// contains the query — finds apps that register nowhere (engines,
    /// dev tools under Program Files). Bounded: depth 6, max 5 hits.</summary>
    IReadOnlyList<string> FindExecutables(string query);
}
