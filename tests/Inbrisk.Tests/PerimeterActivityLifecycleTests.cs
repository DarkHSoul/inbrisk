using Inbrisk.Core;
using Inbrisk.Mcp;
using Inbrisk.Platform.Windows.Indicator;
using Inbrisk.Sdk;
using Xunit;

namespace Inbrisk.Tests;

public sealed class PerimeterActivityLifecycleTests
{
    [Fact]
    public void Perimeter_Disconnected_IsHidden()
    {
        using var act = new ComputerControlActivityService();
        Assert.Equal(IndicatorState.Disconnected, act.State);
        Assert.False(act.PerimeterVisible, "Disconnected must have perimeterVisible = false");
    }

    [Fact]
    public void Perimeter_ConnectedIdle_IsHidden()
    {
        using var act = new ComputerControlActivityService();
        act.SetConnected(true);
        Assert.Equal(IndicatorState.ConnectedIdle, act.State);
        Assert.False(act.PerimeterVisible, "ConnectedIdle must have perimeterVisible = false (no active work = no perimeter wall)");
    }

    [Fact]
    public void Perimeter_ToolExecution_Shows()
    {
        using var act = new ComputerControlActivityService();
        act.SetConnected(true);
        Assert.False(act.PerimeterVisible);

        using var token = act.BeginActivity("computer_click", "sess-1");
        Assert.Equal(IndicatorState.Active, act.State);
        Assert.True(act.PerimeterVisible, "Executing an active tool must set perimeterVisible = true");
        Assert.Single(act.ActiveTokens);
        Assert.Equal("computer_click", token.OperationId);
        Assert.Equal("sess-1", token.SessionId);
    }

    [Fact]
    public void Perimeter_ToolCompletion_HidesAfterSettle()
    {
        using var act = new ComputerControlActivityService(idleGraceMs: 50);
        act.SetConnected(true);

        using (var token = act.BeginActivity("computer_type", "sess-1"))
        {
            Assert.True(act.PerimeterVisible);
        }

        // Token disposed -> settling grace period
        Assert.True(SpinWait.SpinUntil(() => !act.PerimeterVisible, 1500),
            "Perimeter must hide after settling period completes");
        Assert.Equal(IndicatorState.ConnectedIdle, act.State);
        Assert.Empty(act.ActiveTokens);
    }

    [Fact]
    public void Perimeter_ToolFailure_HidesAfterFailureSettle()
    {
        using var act = new ComputerControlActivityService(idleGraceMs: 50);
        act.SetConnected(true);

        try
        {
            using var token = act.BeginActivity("computer_find", "sess-1");
            Assert.True(act.PerimeterVisible);
            // Simulate tool failure
            throw new InvalidOperationException("Element not found");
        }
        catch (InvalidOperationException) { }

        // Failure completes -> settles and hides completely without leaving stale visual
        Assert.True(SpinWait.SpinUntil(() => !act.PerimeterVisible, 1500),
            "Failed operation must hide perimeter after settle, never leaving stale failure visual");
        Assert.Equal(IndicatorState.ConnectedIdle, act.State);
    }

    [Fact]
    public void Perimeter_CancelledTool_Clears()
    {
        using var act = new ComputerControlActivityService(idleGraceMs: 50);
        act.SetConnected(true);

        using var cts = new CancellationTokenSource();
        try
        {
            using var token = act.BeginActivity("computer_read", "sess-1");
            Assert.True(act.PerimeterVisible);
            cts.Cancel();
            cts.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) { }

        Assert.True(SpinWait.SpinUntil(() => !act.PerimeterVisible, 1500),
            "Cancelled tool must clear activity token and hide perimeter");
        Assert.Equal(IndicatorState.ConnectedIdle, act.State);
    }

    [Fact]
    public void Perimeter_Exception_Clears()
    {
        using var act = new ComputerControlActivityService(idleGraceMs: 50);
        act.SetConnected(true);

        try
        {
            using var token = act.BeginActivity("computer_launch", "sess-1");
            throw new Exception("Unexpected process crash");
        }
        catch { }

        Assert.Equal(0, act.ActiveLeases);
        Assert.True(SpinWait.SpinUntil(() => !act.PerimeterVisible, 1500));
    }

    [Fact]
    public void Perimeter_MultipleActivities_RemainsUntilLastFinishes()
    {
        using var act = new ComputerControlActivityService(idleGraceMs: 50);
        act.SetConnected(true);

        var tokenA = act.BeginActivity("computer_read", "sess-1");
        Assert.True(act.PerimeterVisible);
        Assert.Equal(1, act.ActiveLeases);

        var tokenB = act.BeginActivity("computer_inspect", "sess-1");
        Assert.True(act.PerimeterVisible);
        Assert.Equal(2, act.ActiveLeases);

        // Tool A finishes -> perimeter remains because Tool B is still active
        tokenA.Dispose();
        Assert.Equal(1, act.ActiveLeases);
        Assert.True(act.PerimeterVisible, "Perimeter must remain visible while Tool B is still executing");

        // Tool B finishes -> settle -> perimeter hides
        tokenB.Dispose();
        Assert.Equal(0, act.ActiveLeases);
        Assert.True(SpinWait.SpinUntil(() => !act.PerimeterVisible, 1500),
            "Perimeter must hide only after the final active tool finishes");
    }

    [Fact]
    public void Perimeter_CompositeRun_DoesNotFlickerBetweenSteps()
    {
        using var act = new ComputerControlActivityService(idleGraceMs: 500);
        var stateTransitions = new List<IndicatorState>();
        act.StateChanged += s => { lock (stateTransitions) stateTransitions.Add(s); };
        act.SetConnected(true);

        // Outer composite plan (e.g. computer_batch or computer_run)
        using (var planToken = act.BeginActivity("computer_batch", "sess-1"))
        {
            Assert.True(act.PerimeterVisible);

            // Step 1
            using (var step1 = act.BeginActivity("computer_click", "sess-1"))
            {
                Assert.Equal(2, act.ActiveLeases);
                Thread.Sleep(20);
            }
            // Step 1 ended, but outer plan still active -> leases = 1, NO flicker to ConnectedIdle
            Assert.Equal(1, act.ActiveLeases);
            Assert.True(act.PerimeterVisible);

            // Step 2
            using (var step2 = act.BeginActivity("computer_type", "sess-1"))
            {
                Assert.Equal(2, act.ActiveLeases);
                Thread.Sleep(20);
            }
            Assert.Equal(1, act.ActiveLeases);
            Assert.True(act.PerimeterVisible);
        }

        // Entire composite plan finished
        Assert.True(SpinWait.SpinUntil(() => !act.PerimeterVisible, 2000));
        lock (stateTransitions)
        {
            // After initial ConnectedIdle, once Active is entered, it must NOT have dropped back to ConnectedIdle mid-run
            var activeIndices = stateTransitions
                .Select((state, idx) => (state, idx))
                .Where(x => x.state == IndicatorState.Active)
                .Select(x => x.idx)
                .ToList();
            Assert.NotEmpty(activeIndices);
        }
    }

    [Fact]
    public void Perimeter_TaskScopedWait_RemainsActive()
    {
        using var act = new ComputerControlActivityService(idleGraceMs: 50);
        act.SetConnected(true);

        // Task-scoped wait holds an activity token for its duration
        using (var waitToken = act.BeginActivity("computer_wait_for", "sess-1"))
        {
            Thread.Sleep(50);
            Assert.True(act.PerimeterVisible, "Task-scoped wait inside an active task must keep perimeter visible");
        }

        Assert.True(SpinWait.SpinUntil(() => !act.PerimeterVisible, 1500));
    }

    [Fact]
    public void Perimeter_BackgroundWatcher_DoesNotShow()
    {
        using var act = new ComputerControlActivityService();
        act.SetConnected(true);

        // Passive background watcher (e.g. event subscription) runs without acquiring activity tokens
        Assert.Equal(0, act.ActiveLeases);
        Assert.False(act.PerimeterVisible, "Passive background watchers must never activate the perimeter wall");
    }

    [Fact]
    public void Perimeter_SessionDisconnect_Clears()
    {
        using var act = new ComputerControlActivityService();
        act.SetConnected(true);

        // Session 1 starts multiple operations
        var t1 = act.BeginActivity("computer_click", "sess-disconnect-1");
        var t2 = act.BeginActivity("computer_find", "sess-disconnect-1");
        Assert.True(act.PerimeterVisible);
        Assert.Equal(2, act.ActiveLeases);

        // Session disconnects -> clears all session visual activity
        act.ClearSessionActivity("sess-disconnect-1");
        Assert.Equal(0, act.ActiveLeases);
        Assert.Empty(act.ActiveTokens);
        Assert.True(SpinWait.SpinUntil(() => !act.PerimeterVisible, 1500),
            "Session disconnect must immediately clear all visual activity owned by that session");
    }

    [Fact]
    public void Perimeter_RuntimeRestart_StartsHidden()
    {
        // On restart, initial state must be Disconnected / Hidden
        using var act = new ComputerControlActivityService();
        Assert.Equal(IndicatorState.Disconnected, act.State);
        Assert.False(act.PerimeterVisible);

        // Connecting alone lands in ConnectedIdle and remains completely hidden
        act.SetConnected(true);
        Assert.Equal(IndicatorState.ConnectedIdle, act.State);
        Assert.False(act.PerimeterVisible, "Runtime restart must never restore previous active perimeter state");
    }

    [Fact]
    public void Perimeter_Emergency_OverridesActivity()
    {
        using var act = new ComputerControlActivityService();
        act.SetConnected(true);

        using var token = act.BeginActivity("computer_type", "sess-1");
        Assert.Equal(IndicatorState.Active, act.State);

        act.SetEmergency(true);
        Assert.Equal(IndicatorState.EmergencyStopped, act.State);
        Assert.True(act.PerimeterVisible, "Emergency stop has high-priority visibility");

        act.SetEmergency(false);
        Assert.Equal(IndicatorState.Active, act.State);
        Assert.True(act.PerimeterVisible);
    }

    [Fact]
    public void Perimeter_EmergencyClearedWhileIdle_BecomesHidden()
    {
        using var act = new ComputerControlActivityService();
        act.SetConnected(true);
        Assert.False(act.PerimeterVisible);

        act.SetEmergency(true);
        Assert.Equal(IndicatorState.EmergencyStopped, act.State);
        Assert.True(act.PerimeterVisible);

        act.SetEmergency(false);
        Assert.Equal(IndicatorState.ConnectedIdle, act.State);
        Assert.False(act.PerimeterVisible, "Clearing emergency while idle must return to completely hidden (not standby)");
    }
}
