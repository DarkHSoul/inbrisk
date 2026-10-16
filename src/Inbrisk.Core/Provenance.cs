using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Inbrisk.Core;

public enum ProcessOwnership
{
    Agent,
    User,
    System
}

public enum LifecycleIntent
{
    Unknown,
    Ephemeral,
    Reusable,
    UserUseful,
    TaskArtifact
}

public enum CleanupDisposition
{
    KeepOpen,
    CloseWhenDone,
    AskBeforeClose
}

public sealed record LifecycleDecision(
    bool CanClose,
    CleanupDisposition Disposition,
    string? Reason);

public static class LifecycleDecisionEngine
{
    public static LifecycleDecision Decide(
        ProcessOwnership ownership,
        bool openedByAgent,
        LifecycleIntent intent,
        bool isProtected = false,
        bool explicitUserClose = false)
    {
        if (isProtected || ownership == ProcessOwnership.System)
        {
            return new LifecycleDecision(
                CanClose: false,
                Disposition: CleanupDisposition.KeepOpen,
                Reason: "Protected window/system process cannot be closed by automated cleanup.");
        }

        if (ownership == ProcessOwnership.User || !openedByAgent)
        {
            if (explicitUserClose)
            {
                return new LifecycleDecision(
                    CanClose: true,
                    Disposition: CleanupDisposition.CloseWhenDone,
                    Reason: "User explicitly commanded closure of user-owned window.");
            }

            return new LifecycleDecision(
                CanClose: false,
                Disposition: CleanupDisposition.KeepOpen,
                Reason: "User-owned application cannot be closed without explicit user override. When in doubt, leave it open.");
        }

        // Agent-owned resource
        return intent switch
        {
            LifecycleIntent.Ephemeral => new LifecycleDecision(
                CanClose: true,
                Disposition: CleanupDisposition.CloseWhenDone,
                Reason: "Ephemeral helper window may be closed when its task finishes."),

            LifecycleIntent.Reusable => new LifecycleDecision(
                CanClose: true,
                Disposition: CleanupDisposition.KeepOpen,
                Reason: "Reusable application remains open for follow-up actions."),

            LifecycleIntent.UserUseful => new LifecycleDecision(
                CanClose: true,
                Disposition: CleanupDisposition.KeepOpen,
                Reason: "Application with user-useful content (e.g. document, browser, calculator) remains open."),

            LifecycleIntent.TaskArtifact => new LifecycleDecision(
                CanClose: true,
                Disposition: CleanupDisposition.KeepOpen,
                Reason: "Task artifact document/result remains open for user review."),

            _ => new LifecycleDecision(
                CanClose: true,
                Disposition: CleanupDisposition.KeepOpen,
                Reason: "Agent-owned resource with unclassified/unknown intent defaults to KEEP OPEN ('When in doubt, leave it open').")
        };
    }
}

public sealed record ProcessProvenance(
    int Pid,
    string ProcessName,
    long? MainHwnd,
    ProcessOwnership Ownership,
    bool OpenedByAgent,
    bool UsedInCurrentTask,
    DateTimeOffset RegisteredAt,
    string? LaunchArgument = null,
    LifecycleIntent Intent = LifecycleIntent.Unknown,
    // LaunchVerified: true ONLY when RegisterAgentLaunch created this entry.
    // Name-based adoption never sets it, so it is the sole safe basis for a
    // close/terminate policy decision.
    bool LaunchVerified = false)
{
    public bool SafeToClose => OpenedByAgent && Ownership == ProcessOwnership.Agent;
    public CleanupDisposition Disposition => LifecycleDecisionEngine.Decide(Ownership, OpenedByAgent, Intent).Disposition;
}

/// <summary>Ownership as reported by the STRICT (registration-only)
/// probe — see <see cref="IProcessProvenanceService.GetStrictWindowOwnership"/>.
/// <see cref="ProcessOwnership.System"/> deliberately has no member here:
/// for a policy gate "not provably the agent's" must collapse onto User,
/// never onto a third bucket that could be reasoned about as permissive.</summary>
public enum StrictWindowOwnership
{
    /// <summary>No registration for this hwnd — provenance unknown. Every
    /// consumer must treat this as "not the agent's".</summary>
    Unknown,
    /// <summary>Registered by RegisterAgentLaunch for exactly this handle.</summary>
    Agent,
    /// <summary>Registered as pre-existing / owned by the local user.</summary>
    User,
}

public interface IProcessProvenanceService
{
    void RegisterInitial(int pid, string processName, long? hwnd = null);
    void RegisterAgentLaunch(int pid, string processName, long? hwnd = null, string? launchArg = null, LifecycleIntent intent = LifecycleIntent.Unknown);
    void SetLifecycleIntent(long hwnd, LifecycleIntent intent);
    void SetLifecycleIntentProcess(int pid, LifecycleIntent intent);
    void MarkUsedProcess(int pid);
    void MarkUsedWindow(long hwnd);
    ProcessProvenance? GetProvenance(int pid);
    ProcessProvenance? GetProvenanceForHwnd(long hwnd);
    StrictWindowOwnership GetStrictWindowOwnership(long hwnd);
    bool CanAgentClose(long hwnd, out string? reason);
    bool CanAgentCloseProcess(int pid, out string? reason);
    /// <summary>Strict window-ownership probe (see
    /// <see cref="TryGetVerifiedAgentProcessForHwnd"/>): matches only windows
    /// whose live PID is a VERIFIED agent launch — the replacement-window
    /// case. Unlike <see cref="CanAgentClose"/> this performs no protection
    /// check and answers ownership only; false simply means "not provably a
    /// verified agent launch".</summary>
    bool TryGetVerifiedAgentProcessForHwnd(long hwnd, out ProcessProvenance? prov);
    IReadOnlyList<ProcessProvenance> ListProvenance();
}

public sealed class ProcessProvenanceService : IProcessProvenanceService
{
    private readonly ConcurrentDictionary<int, ProcessProvenance> _byPid = new();
    private readonly ConcurrentDictionary<long, int> _hwndToPid = new();
    private readonly ConcurrentDictionary<long, ProcessProvenance> _byHwnd = new();
    private readonly ConcurrentDictionary<string, ProcessProvenance> _launchedProcessNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly IWindowService? _windowService;

    public ProcessProvenanceService(IWindowService? windowService = null)
    {
        _windowService = windowService;
    }

    public void RegisterInitial(int pid, string processName, long? hwnd = null)
    {
        if (hwnd.HasValue) _hwndToPid[hwnd.Value] = pid;
        var prov = new ProcessProvenance(
            Pid: pid,
            ProcessName: processName,
            MainHwnd: hwnd,
            Ownership: ProcessOwnership.User,
            OpenedByAgent: false,
            UsedInCurrentTask: false,
            RegisteredAt: DateTimeOffset.UtcNow
        );
        _byPid.TryAdd(pid, prov);
        if (hwnd.HasValue) _byHwnd.TryAdd(hwnd.Value, prov);
    }

    public void RegisterAgentLaunch(int pid, string processName, long? hwnd = null, string? launchArg = null, LifecycleIntent intent = LifecycleIntent.Unknown)
    {
        if (hwnd.HasValue) _hwndToPid[hwnd.Value] = pid;
        var prov = new ProcessProvenance(
            Pid: pid,
            ProcessName: processName,
            MainHwnd: hwnd,
            Ownership: ProcessOwnership.Agent,
            OpenedByAgent: true,
            UsedInCurrentTask: true,
            RegisteredAt: DateTimeOffset.UtcNow,
            LaunchArgument: launchArg,
            Intent: intent,
            LaunchVerified: true
        );
        _byPid[pid] = prov;
        if (hwnd.HasValue) _byHwnd[hwnd.Value] = prov;
        if (!string.IsNullOrWhiteSpace(processName))
        {
            _launchedProcessNames[processName] = prov;
            var clean = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? processName[..^4] : processName;
            _launchedProcessNames[clean] = prov;
        }
        if (!string.IsNullOrWhiteSpace(launchArg))
        {
            _launchedProcessNames[launchArg] = prov;
            var stem = System.IO.Path.GetFileNameWithoutExtension(launchArg);
            if (!string.IsNullOrWhiteSpace(stem))
                _launchedProcessNames[stem] = prov;
        }
    }

    public void SetLifecycleIntent(long hwnd, LifecycleIntent intent)
    {
        if (_byHwnd.TryGetValue(hwnd, out var hp))
        {
            _byHwnd[hwnd] = hp with { Intent = intent };
        }
        if (_hwndToPid.TryGetValue(hwnd, out var pid) && _byPid.TryGetValue(pid, out var pp))
        {
            _byPid[pid] = pp with { Intent = intent };
        }
    }

    public void SetLifecycleIntentProcess(int pid, LifecycleIntent intent)
    {
        if (_byPid.TryGetValue(pid, out var pp))
        {
            _byPid[pid] = pp with { Intent = intent };
        }
        foreach (var kvp in _byHwnd)
        {
            if (kvp.Value.Pid == pid)
                _byHwnd[kvp.Key] = kvp.Value with { Intent = intent };
        }
    }

    public void MarkUsedProcess(int pid)
    {
        if (_byPid.TryGetValue(pid, out var prov))
        {
            _byPid[pid] = prov with { UsedInCurrentTask = true };
        }
        foreach (var kvp in _byHwnd)
        {
            if (kvp.Value.Pid == pid)
                _byHwnd[kvp.Key] = kvp.Value with { UsedInCurrentTask = true };
        }
    }

    public void MarkUsedWindow(long hwnd)
    {
        if (_byHwnd.TryGetValue(hwnd, out var hp))
        {
            _byHwnd[hwnd] = hp with { UsedInCurrentTask = true };
        }

        if (_hwndToPid.TryGetValue(hwnd, out var pid))
        {
            MarkUsedProcess(pid);
        }
        else if (_windowService?.GetWindow(hwnd) is { } w)
        {
            _hwndToPid[hwnd] = w.Pid;
            if (_byHwnd.TryGetValue(hwnd, out var hp2))
                _byHwnd[hwnd] = hp2 with { UsedInCurrentTask = true };
            if (_byPid.TryGetValue(w.Pid, out var prov))
                _byPid[w.Pid] = prov with { UsedInCurrentTask = true };
            else
                RegisterInitial(w.Pid, w.ProcessName ?? "", hwnd);
        }
    }

    public ProcessProvenance? GetProvenance(int pid)
    {
        if (_byPid.TryGetValue(pid, out var prov))
            return prov;

        try
        {
            var proc = System.Diagnostics.Process.GetProcessById(pid);
            var name = proc.ProcessName;
            if (_launchedProcessNames.TryGetValue(name, out var lp) ||
                _launchedProcessNames.TryGetValue(name + ".exe", out lp))
            {
                prov = new ProcessProvenance(
                    Pid: pid,
                    ProcessName: name,
                    MainHwnd: null,
                    Ownership: ProcessOwnership.Agent,
                    OpenedByAgent: true,
                    UsedInCurrentTask: true,
                    RegisteredAt: DateTimeOffset.UtcNow,
                    LaunchArgument: lp.LaunchArgument,
                    Intent: lp.Intent
                );
                _byPid[pid] = prov;
                return prov;
            }
        }
        catch { }

        return null;
    }

    public ProcessProvenance? GetProvenanceForHwnd(long hwnd)
    {
        if (_byHwnd.TryGetValue(hwnd, out var hwndProv))
            return hwndProv;

        if (_hwndToPid.TryGetValue(hwnd, out var pid) && _byPid.TryGetValue(pid, out var prov))
        {
            // If the process is a shared container like ApplicationFrameHost, don't adopt by PID alone
            if (!string.Equals(prov.ProcessName, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(prov.ProcessName, "ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
            {
                return prov;
            }
        }

        if (_windowService?.GetWindow(hwnd) is { } w)
        {
            _hwndToPid[hwnd] = w.Pid;
            if (_byHwnd.TryGetValue(hwnd, out var p))
                return p;

            // 1. Process name matching (excluding generic hosts)
            if (!string.IsNullOrWhiteSpace(w.ProcessName))
            {
                var clean = w.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? w.ProcessName[..^4] : w.ProcessName;
                if (!string.Equals(clean, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
                {
                    if (_launchedProcessNames.TryGetValue(w.ProcessName, out var lp) ||
                        _launchedProcessNames.TryGetValue(clean, out lp))
                    {
                        var adopted = new ProcessProvenance(
                            Pid: w.Pid,
                            ProcessName: w.ProcessName,
                            MainHwnd: hwnd,
                            Ownership: ProcessOwnership.Agent,
                            OpenedByAgent: true,
                            UsedInCurrentTask: true,
                            RegisteredAt: DateTimeOffset.UtcNow,
                            LaunchArgument: lp.LaunchArgument,
                            Intent: lp.Intent
                        );
                        _byPid[w.Pid] = adopted;
                        _byHwnd[hwnd] = adopted;
                        return adopted;
                    }
                }
            }

            // 2. ApplicationFrameHost or hosted window matching by Title or LaunchArgument
            foreach (var registered in _launchedProcessNames.Values)
            {
                if (registered.OpenedByAgent)
                {
                    bool titleMatch = !string.IsNullOrWhiteSpace(w.Title) &&
                        (!string.IsNullOrWhiteSpace(registered.LaunchArgument) && w.Title.Contains(registered.LaunchArgument, StringComparison.OrdinalIgnoreCase) ||
                         !string.IsNullOrWhiteSpace(registered.ProcessName) && w.Title.Contains(registered.ProcessName, StringComparison.OrdinalIgnoreCase));

                    if (titleMatch)
                    {
                        var adopted = new ProcessProvenance(
                            Pid: w.Pid,
                            ProcessName: w.ProcessName,
                            MainHwnd: hwnd,
                            Ownership: ProcessOwnership.Agent,
                            OpenedByAgent: true,
                            UsedInCurrentTask: true,
                            RegisteredAt: DateTimeOffset.UtcNow,
                            LaunchArgument: registered.LaunchArgument,
                            Intent: registered.Intent
                        );
                        _byHwnd[hwnd] = adopted;
                        return adopted;
                    }
                }
            }

            if (_byPid.TryGetValue(w.Pid, out var existing))
            {
                if (!string.Equals(existing.ProcessName, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(existing.ProcessName, "ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
                {
                    return existing;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Ownership for a SECURITY decision (safety policy gating).
    ///
    /// <see cref="GetProvenanceForHwnd"/> deliberately ADOPTS windows by
    /// process-name / title similarity when the handle itself is unknown
    /// (line ~305). That heuristic is right for cleanup — better to
    /// recognise an app's replacement window — but far too loose for a
    /// policy gate: once the agent has launched "notepad" once, the
    /// process-name index would answer Agent for every Notepad window the
    /// USER opens afterwards, and the gate would wave through writes into
    /// the user's own documents.
    ///
    /// So this probe answers from the hwnd-keyed registration table only.
    /// The sole path to <see cref="StrictWindowOwnership.Agent"/> is a
    /// <see cref="RegisterAgentLaunch"/> call that named exactly this
    /// handle — which, in the MCP launch path, only happens after
    /// LaunchCreationEvidenceEvaluator proved positive creation evidence
    /// (new pid AND new hwnd AND start-time after the pre-launch
    /// snapshot). A reused "ExistingInstance" window is never registered,
    /// so it never answers Agent.
    ///
    /// Unknown handle → <see cref="StrictWindowOwnership.Unknown"/>, which
    /// every caller must read as "not the agent's".
    /// </summary>
    public StrictWindowOwnership GetStrictWindowOwnership(long hwnd)
    {
        if (!_byHwnd.TryGetValue(hwnd, out var prov)) return StrictWindowOwnership.Unknown;
        return prov.Ownership == ProcessOwnership.Agent && prov.OpenedByAgent
            ? StrictWindowOwnership.Agent
            : StrictWindowOwnership.User;
    }

    public bool CanAgentClose(long hwnd, out string? reason)
    {
        if (_windowService != null && _windowService.IsWindowProtected(hwnd, out var protReason))
        {
            reason = $"Window is protected by safety policy: {protReason}";
            return false;
        }

        // Security gate. GetProvenanceForHwnd deliberately ADOPTS windows by
        // process-name / title similarity, which is right for cleanup but far
        // too loose for a policy gate: once the agent launched "notepad" once,
        // the name index would answer Agent for EVERY Notepad window the user
        // opens afterwards. Accept only:
        //   1. the exact hwnd named by RegisterAgentLaunch, or
        //   2. a window whose live PID is a VERIFIED agent launch (a
        //      replacement window of the process we spawned - same PID). A
        //      different process instance the user opens later has a
        //      different PID and is therefore never adopted.
        var strict = GetStrictWindowOwnership(hwnd);
        if (strict == StrictWindowOwnership.Agent)
        {
            reason = null;
            return true;
        }

        if (strict == StrictWindowOwnership.User)
        {
            reason = "Window belongs to a pre-existing user application (ownership: user). Inbrisk Application Lifecycle Policy prevents closing applications not explicitly launched by the agent. When in doubt, leave it open.";
            return false;
        }

        if (TryGetVerifiedAgentProcessForHwnd(hwnd, out var prov) && prov != null)
            return CanAgentCloseCore(prov, out reason);

        // Golden rule: When in doubt, leave it open.
        reason = "Application provenance is unknown (not opened by agent). Inbrisk Application Lifecycle Policy prevents closing applications not explicitly launched by the agent. When in doubt, leave it open.";
        return false;
    }

    public bool CanAgentCloseProcess(int pid, out string? reason)
    {
        if (_windowService != null)
        {
            try
            {
                foreach (var w in _windowService.ListWindows())
                {
                    if (w.Pid == pid && _windowService.IsWindowProtected(w.Hwnd, out var protReason))
                    {
                        reason = $"Process is protected by safety policy: {protReason}";
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                // Fail CLOSED: if the protection probe cannot run we cannot
                // prove the process is unprotected, so refuse. Previously the
                // catch swallowed the exception and execution fell through to
                // GetProvenance(pid), which adopts a process by NAME — letting
                // an enumeration failure escalate into a name-based allow.
                System.Diagnostics.Debug.WriteLine($"CanAgentCloseProcess protection probe pid={pid} failed: {ex.Message}");
                reason = $"Process PID {pid} protection could not be verified (window enumeration failed); refusing to close.";
                return false;
            }
        }

        // Strict: only a VERIFIED agent launch may be closed. GetProvenance
        // adopts by process name (a Notepad the user opened after the agent
        // launched one shares the name), so it must not gate a close.
        if (!_byPid.TryGetValue(pid, out var prov) || !prov.LaunchVerified)
        {
            reason = $"Process PID {pid} was not launched by agent. Inbrisk Application Lifecycle Policy prevents closing applications not explicitly launched by the agent. When in doubt, leave it open.";
            return false;
        }

        return CanAgentCloseCore(prov, out reason);
    }

    /// <summary>
    /// Strict provenance query: resolve a window to a process the agent
    /// VERIFIABLY launched (same live PID). Unlike <see cref="CanAgentClose"/>
    /// (a POLICY gate: protection + ownership), this answers "who owns this
    /// window" only — callers that already gate protection separately (e.g.
    /// window-ownership probes for cleanup/selection) should use this and
    /// <see cref="GetStrictWindowOwnership"/> directly. Replacement windows
    /// of a process we spawned match; a *different* instance of the same app
    /// (a Notepad the user opens later) is never matched.
    /// </summary>
    public bool TryGetVerifiedAgentProcessForHwnd(long hwnd, out ProcessProvenance? prov)
    {
        prov = null;

        int pid = 0;
        string? title = null;
        try
        {
            var win = _windowService?.GetWindow(hwnd);
            if (win != null)
            {
                pid = win.Pid;
                title = win.Title;
            }
        }
        catch { }

        if (pid == 0 && _hwndToPid.TryGetValue(hwnd, out var mappedPid))
            pid = mappedPid;

        if (pid == 0)
            return false;

        if (!_byPid.TryGetValue(pid, out var p) ||
            !p.LaunchVerified ||
            !p.OpenedByAgent ||
            p.Ownership != ProcessOwnership.Agent)
            return false;

        // Shared host containers (ApplicationFrameHost and friends) multiplex
        // many unrelated apps under ONE pid, so a pid match alone would let a
        // user window hosted by the same process inherit agent ownership.
        // Require the window title to match the identity we launched.
        if (IsSharedHostContainer(p.ProcessName))
        {
            var t = title ?? "";
            if (string.IsNullOrWhiteSpace(p.LaunchArgument) ||
                !t.Contains(p.LaunchArgument, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        prov = p;
        return true;
    }

    private static bool IsSharedHostContainer(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        var clean = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4] : processName;
        return clean.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase);
    }

    private static bool CanAgentCloseCore(ProcessProvenance prov, out string? reason)
    {
        if (!prov.OpenedByAgent || prov.Ownership != ProcessOwnership.Agent)
        {
            reason = $"Application '{prov.ProcessName}' (PID {prov.Pid}) was not launched by the agent (ownership: user). Inbrisk Application Lifecycle Policy prevents closing pre-existing user applications. When in doubt, leave it open.";
            return false;
        }

        reason = null;
        return true;
    }

    public IReadOnlyList<ProcessProvenance> ListProvenance()
        => _byPid.Values.ToList();
}
