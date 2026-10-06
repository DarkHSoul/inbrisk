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
    LifecycleIntent Intent = LifecycleIntent.Unknown)
{
    public bool SafeToClose => OpenedByAgent && Ownership == ProcessOwnership.Agent;
    public CleanupDisposition Disposition => LifecycleDecisionEngine.Decide(Ownership, OpenedByAgent, Intent).Disposition;
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
    bool CanAgentClose(long hwnd, out string? reason);
    bool CanAgentCloseProcess(int pid, out string? reason);
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
            Intent: intent
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

    public bool CanAgentClose(long hwnd, out string? reason)
    {
        if (_windowService != null && _windowService.IsWindowProtected(hwnd, out var protReason))
        {
            reason = $"Window is protected by safety policy: {protReason}";
            return false;
        }

        var prov = GetProvenanceForHwnd(hwnd);
        if (prov == null)
        {
            // Golden rule: When in doubt, leave it open.
            reason = "Application provenance is unknown (not opened by agent). Inbrisk Application Lifecycle Policy prevents closing applications not explicitly launched by the agent. When in doubt, leave it open.";
            return false;
        }

        return CanAgentCloseCore(prov, out reason);
    }

    public bool CanAgentCloseProcess(int pid, out string? reason)
    {
        var prov = GetProvenance(pid);
        if (prov == null)
        {
            reason = $"Process PID {pid} was not launched by agent. Inbrisk Application Lifecycle Policy prevents closing applications not explicitly launched by the agent. When in doubt, leave it open.";
            return false;
        }

        return CanAgentCloseCore(prov, out reason);
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
