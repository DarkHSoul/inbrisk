using System.Text.Json;
using Inbrisk.Core;
using Inbrisk.Mcp;
using Inbrisk.Runtime;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Inbrisk.Tests;

public class DesktopArbiterTests
{
    private static InbriskTools CreateToolsWithArbiter(DesktopArbiter arbiter)
    {
        var session = (McpSession)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(McpSession));
        typeof(McpSession).GetField("<SessionId>k__BackingField",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(session, "test-arbiter-session");
        typeof(McpSession).GetField("<Arbiter>k__BackingField",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(session, arbiter);
        typeof(McpSession).GetField("<SessionCts>k__BackingField",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(session, new CancellationTokenSource());

        var control = (EmergencyControl)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(EmergencyControl));
        typeof(EmergencyControl).GetField("_state",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(control, ComputerControlState.Active);
        typeof(EmergencyControl).GetField("_epoch",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(control, new CancellationTokenSource());
        typeof(EmergencyControl).GetField("_peerAuthority",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(control, true);
        typeof(EmergencyControl).GetField("_gate",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(control, new object());
        typeof(McpSession).GetField("<Control>k__BackingField",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(session, control);

        var tools = (InbriskTools)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(InbriskTools));
        typeof(InbriskTools).GetField("_s",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(tools, session);
        return tools;
    }

    [Fact]
    public async Task DesktopArbiter_AcquirePhysicalLease_EnforcesExclusivity()
    {
        using var arbiter = new DesktopArbiter();

        // Task A acquires physical lease
        await using var leaseA = await arbiter.AcquireAsync("taskA", LeaseKind.PhysicalInput, "Task A typing");
        Assert.True(leaseA.IsActive);
        Assert.Equal("taskA", leaseA.OwnerId);

        var owner = arbiter.GetExclusiveOwner();
        Assert.NotNull(owner);
        Assert.Equal("taskA", owner!.OwnerId);

        // Task B tries to acquire physical lease with 50ms timeout -> must time out
        await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await arbiter.AcquireAsync("taskB", LeaseKind.PhysicalInput, "Task B clicking", timeout: TimeSpan.FromMilliseconds(50));
        });

        // Release lease A
        leaseA.Release();
        Assert.False(leaseA.IsActive);
        Assert.Null(arbiter.GetExclusiveOwner());

        // Now Task B can acquire immediately
        await using var leaseB = await arbiter.AcquireAsync("taskB", LeaseKind.PhysicalInput, "Task B clicking", timeout: TimeSpan.FromMilliseconds(500));
        Assert.True(leaseB.IsActive);
        Assert.Equal("taskB", leaseB.OwnerId);
    }

    [Fact]
    public async Task DesktopArbiter_ReadOnlyLeases_AllowConcurrentAccess()
    {
        using var arbiter = new DesktopArbiter();

        await using var readLease1 = await arbiter.AcquireAsync("agent1", LeaseKind.ReadOnly, "Agent 1 observe");
        await using var readLease2 = await arbiter.AcquireAsync("agent2", LeaseKind.ReadOnly, "Agent 2 inspect");

        Assert.True(readLease1.IsActive);
        Assert.True(readLease2.IsActive);

        var leases = arbiter.GetActiveLeases();
        Assert.Equal(2, leases.Count);
        Assert.Contains(leases, l => l.OwnerId == "agent1");
        Assert.Contains(leases, l => l.OwnerId == "agent2");

        // Neither is exclusive owner
        Assert.Null(arbiter.GetExclusiveOwner());
    }

    [Fact]
    public async Task DesktopArbiter_CancelTask_OnlyCancelsSpecificTask()
    {
        using var arbiter = new DesktopArbiter();

        await using var leaseA = await arbiter.AcquireAsync("taskA", LeaseKind.PhysicalInput, "Task A work");
        await using var leaseB = await arbiter.AcquireAsync("taskB", LeaseKind.ReadOnly, "Task B work");

        var tokenA = leaseA.CancellationToken;
        var tokenB = leaseB.CancellationToken;

        Assert.False(tokenA.IsCancellationRequested);
        Assert.False(tokenB.IsCancellationRequested);

        // Cancel task A only
        var cancelled = arbiter.CancelTask("taskA", "user interrupted task A");
        Assert.True(cancelled);

        // Task A is cancelled and its lease revoked
        Assert.True(tokenA.IsCancellationRequested);
        Assert.False(leaseA.IsActive);

        // Task B is still running unaffected
        Assert.False(tokenB.IsCancellationRequested);
        Assert.True(leaseB.IsActive);
    }

    [Fact]
    public async Task DesktopArbiter_CancelAll_EmergencyStopsAllAndTriggersCleanup()
    {
        using var arbiter = new DesktopArbiter();
        bool cleanupCalled = false;
        arbiter.OnInputCleanup = () => cleanupCalled = true;

        await using var leaseA = await arbiter.AcquireAsync("taskA", LeaseKind.PhysicalInput, "Task A work");
        await using var leaseB = await arbiter.AcquireAsync("taskB", LeaseKind.ReadOnly, "Task B work");

        arbiter.CancelAll("Emergency stop initiated");

        Assert.True(cleanupCalled);
        Assert.True(leaseA.CancellationToken.IsCancellationRequested);
        Assert.True(leaseB.CancellationToken.IsCancellationRequested);
        Assert.False(leaseA.IsActive);
        Assert.False(leaseB.IsActive);
        Assert.Empty(arbiter.GetActiveLeases());
    }

    [Fact]
    public async Task DesktopArbiter_Watchdog_ExpiresStaleLeaseAutomatically()
    {
        using var arbiter = new DesktopArbiter();
        bool cleanupCalled = false;
        arbiter.OnInputCleanup = () => cleanupCalled = true;

        // Acquire lease with 100ms duration
        var lease = await arbiter.AcquireAsync(
            "hungTask",
            LeaseKind.PhysicalInput,
            "Hung script",
            leaseDuration: TimeSpan.FromMilliseconds(100));

        Assert.True(lease.IsActive);

        // Wait ~1.5s for watchdog tick (runs every 1s)
        await Task.Delay(1300);

        // Watchdog must have expired the lease
        Assert.False(lease.IsActive);
        Assert.True(cleanupCalled);
        Assert.Null(arbiter.GetExclusiveOwner());

        // Physical input is now available for new task
        await using var freshLease = await arbiter.AcquireAsync("freshTask", LeaseKind.PhysicalInput, "Fresh action");
        Assert.True(freshLease.IsActive);
    }

    [Fact]
    public async Task InbriskTools_CancelTaskAndLeasesStatus_WorkCorrectly()
    {
        using var arbiter = new DesktopArbiter();
        var tools = CreateToolsWithArbiter(arbiter);

        // 1. Initially no leases
        var status1 = tools.LeasesStatus();
        Assert.False(status1.IsError);
        var text1 = ((TextContentBlock)status1.Content[0]).Text;
        using (var doc1 = JsonDocument.Parse(text1))
        {
            Assert.False(doc1.RootElement.GetProperty("hasExclusiveOwner").GetBoolean());
            Assert.Equal(0, doc1.RootElement.GetProperty("activeLeasesCount").GetInt32());
        }

        // 2. Acquire a lease on behalf of a plan run
        var lease = await arbiter.AcquireAsync("run_123", LeaseKind.PhysicalInput, "Plan step 2: click");

        var status2 = tools.LeasesStatus();
        var text2 = ((TextContentBlock)status2.Content[0]).Text;
        using (var doc2 = JsonDocument.Parse(text2))
        {
            Assert.True(doc2.RootElement.GetProperty("hasExclusiveOwner").GetBoolean());
            Assert.Equal("run_123", doc2.RootElement.GetProperty("exclusiveOwner").GetProperty("OwnerId").GetString());
            Assert.Equal(1, doc2.RootElement.GetProperty("activeLeasesCount").GetInt32());
        }

        // 3. Cancel task via MCP tool
        var cancelResult = tools.CancelTask("run_123", "cancel requested by user");
        Assert.False(cancelResult.IsError);
        var cancelText = ((TextContentBlock)cancelResult.Content[0]).Text;
        using (var cancelDoc = JsonDocument.Parse(cancelText))
        {
            Assert.True(cancelDoc.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("run_123", cancelDoc.RootElement.GetProperty("taskId").GetString());
        }

        // 4. Verify lease is released
        Assert.False(lease.IsActive);
    }

    [Fact]
    public async Task ActChain_ReturnsConcurrencyConflict_WhenPhysicalInputHeldByOtherTask()
    {
        using var arbiter = new DesktopArbiter();
        var tools = CreateToolsWithArbiter(arbiter);

        // Task A holds physical lease
        await using var externalLease = await arbiter.AcquireAsync("another_ai_agent", LeaseKind.PhysicalInput, "Agent writing to Spotify");

        // ActChain trying to perform a physical action with a 50ms lease timeout
        var actions = new List<AgentAction>
        {
            new(AgentActionKind.Click, ElementId: "el_1", Ms: 50)
        };

        // Invoke reflection call to private ActChain
        var method = typeof(InbriskTools).GetMethod("ActChain",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);

        var resultTask = (Task<CallToolResult>)method!.Invoke(tools, new object?[]
        {
            actions, "el_1", CancellationToken.None, false
        })!;

        var result = await resultTask;
        Assert.True(result.IsError);
        var text = ((TextContentBlock)result.Content[0]).Text;
        using var doc = JsonDocument.Parse(text);
        Assert.Equal("ConcurrencyConflict", doc.RootElement.GetProperty("error").GetString());
        Assert.Contains("another_ai_agent", doc.RootElement.GetProperty("detail").GetString());
    }
}
