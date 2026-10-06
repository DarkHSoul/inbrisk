using Inbrisk.Core;
using Xunit;

namespace Inbrisk.Tests;

public sealed class ConservativeLifecycleTests
{
    private sealed class StubWindowService : IWindowService
    {
        public List<WindowInfo> Windows { get; } = new();
        public IReadOnlyList<WindowInfo> ListWindows() => Windows;
        public WindowInfo? GetWindow(long hwnd) => Windows.FirstOrDefault(w => w.Hwnd == hwnd);
        public WindowInfo? GetForegroundWindow() => Windows.FirstOrDefault(w => w.IsForeground);
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
            if (hwnd == 0x9999)
            {
                reason = "protected IDE session";
                return true;
            }
            return false;
        }
    }

    [Fact]
    public void AgentOwned_Ephemeral_ClosesWhenPurposeEnds()
    {
        var decision = LifecycleDecisionEngine.Decide(
            ownership: ProcessOwnership.Agent,
            openedByAgent: true,
            intent: LifecycleIntent.Ephemeral);

        Assert.True(decision.CanClose);
        Assert.Equal(CleanupDisposition.CloseWhenDone, decision.Disposition);
    }

    [Fact]
    public void AgentOwned_Reusable_RemainsOpenAfterTask()
    {
        // Reusable tools like Calculator or browser instance stay open
        var decision = LifecycleDecisionEngine.Decide(
            ownership: ProcessOwnership.Agent,
            openedByAgent: true,
            intent: LifecycleIntent.Reusable);

        Assert.True(decision.CanClose, "Agent has authority/permission to close if user explicitly commands");
        Assert.Equal(CleanupDisposition.KeepOpen, decision.Disposition); // but disposition is keep open
    }

    [Fact]
    public void AgentOwned_TaskArtifact_RemainsOpenAfterTask()
    {
        // Notepad document or editor with task output remains open for user review
        var decision = LifecycleDecisionEngine.Decide(
            ownership: ProcessOwnership.Agent,
            openedByAgent: true,
            intent: LifecycleIntent.TaskArtifact);

        Assert.True(decision.CanClose);
        Assert.Equal(CleanupDisposition.KeepOpen, decision.Disposition);
    }

    [Fact]
    public void AgentOwned_ResultExplorer_RemainsOpen()
    {
        // Result folder opened in Explorer remains open for user access
        var decision = LifecycleDecisionEngine.Decide(
            ownership: ProcessOwnership.Agent,
            openedByAgent: true,
            intent: LifecycleIntent.UserUseful);

        Assert.True(decision.CanClose);
        Assert.Equal(CleanupDisposition.KeepOpen, decision.Disposition);
    }

    [Fact]
    public void UserOwned_NeverClosedByAutomaticCleanup()
    {
        var decision = LifecycleDecisionEngine.Decide(
            ownership: ProcessOwnership.User,
            openedByAgent: false,
            intent: LifecycleIntent.Unknown,
            explicitUserClose: false);

        Assert.False(decision.CanClose);
        Assert.Equal(CleanupDisposition.KeepOpen, decision.Disposition);
    }

    [Fact]
    public void Protected_NeverClosedByAutomaticCleanup()
    {
        var decision = LifecycleDecisionEngine.Decide(
            ownership: ProcessOwnership.Agent,
            openedByAgent: true,
            intent: LifecycleIntent.Ephemeral,
            isProtected: true);

        Assert.False(decision.CanClose);
        Assert.Equal(CleanupDisposition.KeepOpen, decision.Disposition);
    }

    [Fact]
    public void UnknownAgentOwned_DefaultsToKeepOpen()
    {
        // "When in doubt, leave it open"
        var decision = LifecycleDecisionEngine.Decide(
            ownership: ProcessOwnership.Agent,
            openedByAgent: true,
            intent: LifecycleIntent.Unknown);

        Assert.True(decision.CanClose);
        Assert.Equal(CleanupDisposition.KeepOpen, decision.Disposition);
        Assert.Contains("When in doubt, leave it open", decision.Reason);
    }

    [Fact]
    public void ExplicitCloseEverything_ClosesEligibleAgentResources()
    {
        // When user explicitly commands close-everything, eligible agent resources can be closed
        var ephemeral = LifecycleDecisionEngine.Decide(ProcessOwnership.Agent, true, LifecycleIntent.Ephemeral, explicitUserClose: true);
        Assert.True(ephemeral.CanClose);
        Assert.Equal(CleanupDisposition.CloseWhenDone, ephemeral.Disposition);

        var reusable = LifecycleDecisionEngine.Decide(ProcessOwnership.Agent, true, LifecycleIntent.Reusable, explicitUserClose: true);
        Assert.True(reusable.CanClose);
    }

    [Fact]
    public void ExplicitCloseEverything_DoesNotCloseUserOrProtected()
    {
        var protectedDecision = LifecycleDecisionEngine.Decide(
            ownership: ProcessOwnership.Agent,
            openedByAgent: true,
            intent: LifecycleIntent.Ephemeral,
            isProtected: true,
            explicitUserClose: true);

        Assert.False(protectedDecision.CanClose);
        Assert.Equal(CleanupDisposition.KeepOpen, protectedDecision.Disposition);

        var userDecision = LifecycleDecisionEngine.Decide(
            ownership: ProcessOwnership.User,
            openedByAgent: false,
            intent: LifecycleIntent.Unknown,
            isProtected: false,
            explicitUserClose: false);

        Assert.False(userDecision.CanClose);
        Assert.Equal(CleanupDisposition.KeepOpen, userDecision.Disposition);
    }

    [Fact]
    public void Lifecycle_EphemeralBecomesUseful_IsNotClosed()
    {
        var winService = new StubWindowService();
        var prov = new ProcessProvenanceService(winService);

        var win = new WindowInfo(0x3333, 4444, "Helper Tool", "helper.exe",
            new RectPx(10, 10, 300, 200), WindowState.Normal, false, false, true, 0);
        winService.Windows.Add(win);

        // Initially launched as Ephemeral helper
        prov.RegisterAgentLaunch(4444, "helper.exe", 0x3333, intent: LifecycleIntent.Ephemeral);
        var initialProv = prov.GetProvenanceForHwnd(0x3333);
        Assert.NotNull(initialProv);
        Assert.Equal(LifecycleIntent.Ephemeral, initialProv.Intent);
        Assert.Equal(CleanupDisposition.CloseWhenDone, initialProv.Disposition);

        // Promoted to UserUseful during execution (e.g. user generated content in it)
        prov.SetLifecycleIntent(0x3333, LifecycleIntent.UserUseful);
        var updatedProv = prov.GetProvenanceForHwnd(0x3333);
        Assert.NotNull(updatedProv);
        Assert.Equal(LifecycleIntent.UserUseful, updatedProv.Intent);
        Assert.Equal(CleanupDisposition.KeepOpen, updatedProv.Disposition);
    }
}
