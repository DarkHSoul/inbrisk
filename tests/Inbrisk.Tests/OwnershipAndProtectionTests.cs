using System.Diagnostics;
using System.Text.Json;
using Inbrisk.Core;
using Inbrisk.Mcp;
using Inbrisk.Platform.Windows.Apps;
using Inbrisk.Platform.Windows.Topology;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Inbrisk.Tests;

public sealed class OwnershipAndProtectionTests
{
    private sealed class FakeWindowService : IWindowService
    {
        public List<WindowInfo> WindowsList { get; } = new();
        public IReadOnlyList<WindowInfo> ListWindows() => WindowsList;
        public WindowInfo? GetWindow(long hwnd) => WindowsList.FirstOrDefault(w => w.Hwnd == hwnd);
        public WindowInfo? GetForegroundWindow() => WindowsList.FirstOrDefault(w => w.IsForeground);
        public IReadOnlyList<MonitorInfo> GetMonitors() => [];
        public RectPx GetVirtualDesktopBounds() => new(0, 0, 1920, 1080);
        public bool FocusWindow(long hwnd) => true;
        public bool CloseWindow(long hwnd) => true;
        public WindowInfo? GetModalPopup(long hwnd) => null;
        public bool IsWindowEnabled(long hwnd) => true;
        public IReadOnlyList<WindowInfo> FindSystemDialogs() => [];
        public bool IsWindowProtected(long hwnd, out string? reason)
        {
            reason = null;
            var w = GetWindow(hwnd);
            if (w == null) return false;
            if (w.Title.Equals("Program Manager", StringComparison.OrdinalIgnoreCase) ||
                w.ProcessName.Equals("Progman", StringComparison.OrdinalIgnoreCase))
            {
                reason = "protected Windows desktop shell";
                return true;
            }
            if (w.ProcessName.Equals("inbrisk.exe", StringComparison.OrdinalIgnoreCase) ||
                w.ProcessName.Equals("antigravity.exe", StringComparison.OrdinalIgnoreCase))
            {
                reason = "agent runtime process";
                return true;
            }
            if (w.ProcessName.Equals("devin.exe", StringComparison.OrdinalIgnoreCase))
            {
                reason = "protected host IDE";
                return true;
            }
            return false;
        }
    }

    [Fact]
    public void Launch_AumidHostedWindow_RemainsAgentOwnedAcrossHwndReplacement()
    {
        var winService = new FakeWindowService();
        var prov = new ProcessProvenanceService(winService);

        // Pre-launch snapshot: ApplicationFrameHost already running with PID 5000
        var existingPids = new Dictionary<int, DateTimeOffset?>
        {
            [5000] = DateTimeOffset.UtcNow.AddMinutes(-10)
        };
        var existingHwnds = new HashSet<long> { 0x0100 };
        var preSnapshot = new PreLaunchSnapshot(DateTimeOffset.UtcNow, existingHwnds, existingPids);

        // 1. Initial launch creates HWND 0x1001 hosted under ApplicationFrameHost (PID 5000)
        var initialHwnd = 0x1001;
        var hostPid = 5000;
        var initialWin = new WindowInfo(initialHwnd, hostPid, "Calculator", "ApplicationFrameHost.exe",
            new RectPx(100, 100, 400, 600), WindowState.Normal, false, false, true, 0);
        winService.WindowsList.Add(initialWin);

        // Evaluate ownership using LaunchCreationEvidenceEvaluator with isHostedProcess: true
        bool agentOwned = LaunchCreationEvidenceEvaluator.EvaluateAgentOwnership(
            preSnapshot: preSnapshot,
            finalHwnd: initialHwnd,
            finalPid: hostPid,
            finalPidStartTime: existingPids[5000],
            spawnedPid: null,
            launchState: "Ready",
            hasExplicitProcessSpawn: false,
            isHostedProcess: true
        );

        Assert.True(agentOwned, "Newly created HWND hosted under ApplicationFrameHost must be evaluated as agent-owned.");

        // Register launch in McpSession and Provenance
        using var control = new EmergencyControl();
        var session = new McpSession(control);
        session.RecordWindowLaunch(
            hwnd: initialHwnd,
            pid: hostPid,
            appIdentity: "Calculator",
            title: "Calculator",
            agentOwned: true,
            alreadyRunning: false,
            intent: LifecycleIntent.Reusable
        );
        prov.RegisterAgentLaunch(hostPid, "ApplicationFrameHost.exe", initialHwnd, "Calculator", LifecycleIntent.Reusable);

        // 2. Calculator replaces its HWND: 0x1001 is destroyed, replaced by 0x1002 with same app identity/title
        winService.WindowsList.Remove(initialWin);
        var replacementHwnd = 0x1002;
        var replacementWin = new WindowInfo(replacementHwnd, hostPid, "Calculator", "ApplicationFrameHost.exe",
            new RectPx(100, 100, 400, 600), WindowState.Normal, false, false, true, 0);
        winService.WindowsList.Add(replacementWin);

        // Verify provenance recognizes replacement HWND as agent-owned
        var repProv = prov.GetProvenanceForHwnd(replacementHwnd);
        Assert.NotNull(repProv);
        Assert.True(repProv.OpenedByAgent, "Replacement HWND must inherit openedByAgent = true");
        Assert.Equal(ProcessOwnership.Agent, repProv.Ownership);

        // Verify normal close succeeds WITHOUT force
        Assert.True(prov.CanAgentClose(replacementHwnd, out var denyReason), denyReason ?? "Close was denied");
        Assert.Null(denyReason);
    }

    [Fact]
    public void Protection_TitleContainingInbrisk_NotProtectedByTitleAlone()
    {
        var winService = new FakeWindowService();
        var notepadDogfood = new WindowInfo(0x5555, 1234, "Inbrisk_Final_Dogfood.txt - Notepad", "notepad.exe",
            new RectPx(100, 100, 500, 400), WindowState.Normal, false, false, true, 0);
        winService.WindowsList.Add(notepadDogfood);

        // Process name is notepad.exe, title is "Inbrisk_Final_Dogfood.txt - Notepad"
        bool isProcProtected = WindowService.IsProcessProtected(notepadDogfood.ProcessName, out var procReason);
        Assert.False(isProcProtected, "notepad.exe must not be protected as a process.");

        bool isWinProtected = winService.IsWindowProtected(notepadDogfood.Hwnd, out var winReason);
        Assert.False(isWinProtected, "Window title containing 'Inbrisk' must not be protected by title alone.");
    }

    [Fact]
    public void Protection_ActualAgentRuntimeWindow_RemainsProtected()
    {
        var winService = new FakeWindowService();
        var inbriskWin = new WindowInfo(0x7777, 9999, "Inbrisk Assistant", "inbrisk.exe",
            new RectPx(0, 0, 800, 600), WindowState.Normal, false, false, true, 0);
        winService.WindowsList.Add(inbriskWin);

        Assert.True(winService.IsWindowProtected(0x7777, out var reason));
        Assert.NotNull(reason);
        Assert.True(WindowService.IsProcessProtected("inbrisk.exe", out _));
    }

    [Fact]
    public void Protection_DevinIDE_RemainsProtected()
    {
        var winService = new FakeWindowService();
        var devinWin = new WindowInfo(0x8888, 8888, "Devin Workspace", "devin.exe",
            new RectPx(0, 0, 1200, 800), WindowState.Normal, false, false, true, 0);
        winService.WindowsList.Add(devinWin);

        Assert.True(winService.IsWindowProtected(0x8888, out var reason));
        Assert.NotNull(reason);
        Assert.True(WindowService.IsProcessProtected("devin.exe", out _));
    }

    [Fact]
    public void Protection_ProgramManager_RemainsProtected()
    {
        var winService = new FakeWindowService();
        var progmanWin = new WindowInfo(0x0001, 100, "Program Manager", "Progman",
            new RectPx(0, 0, 1920, 1080), WindowState.Normal, false, false, true, 0);
        winService.WindowsList.Add(progmanWin);

        Assert.True(winService.IsWindowProtected(0x0001, out var reason));
        Assert.NotNull(reason);
    }

    [Fact]
    public void Close_UserOwned_NoExplicitOverride_RemainsDenied()
    {
        var winService = new FakeWindowService();
        var prov = new ProcessProvenanceService(winService);

        var userExcel = new WindowInfo(0x4000, 3333, "Financials.xlsx - Excel", "excel.exe",
            new RectPx(100, 100, 900, 700), WindowState.Normal, false, false, true, 0);
        winService.WindowsList.Add(userExcel);

        // Pre-existing user application discovered before task
        prov.RegisterInitial(3333, "excel.exe", 0x4000);

        // Attempting to close without explicit force/override must be denied
        bool canClose = prov.CanAgentClose(0x4000, out var reason);
        Assert.False(canClose);
        Assert.NotNull(reason);
        Assert.Contains("ownership: user", reason);
        Assert.Contains("When in doubt, leave it open", reason);
    }
}
