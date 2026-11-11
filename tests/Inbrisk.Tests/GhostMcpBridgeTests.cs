using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Inbrisk.Core;
using Inbrisk.Mcp;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace Inbrisk.Tests;

public sealed class GhostMcpBridgeTests
{
    [Fact]
    public void GhostMcpBridge_ToolSchemas_ExposeRequiredGhostTools()
    {
        var methods = typeof(GhostMcpBridge).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null)
            .ToDictionary(
                m => m.GetCustomAttribute<McpServerToolAttribute>()!.Name,
                m => m,
                StringComparer.OrdinalIgnoreCase);

        // 1. Verify required tool names exist
        Assert.Contains("computer_ghost_start", methods.Keys);
        Assert.Contains("computer_ghost_stop", methods.Keys);
        Assert.Contains("computer_ghost_batch", methods.Keys);
        Assert.Contains("computer_ghost_control", methods.Keys);
        Assert.Contains("ghost_control_open", methods.Keys);

        // 2. Verify computer_ghost_start parameters
        var startMethod = methods["computer_ghost_start"];
        var startParams = startMethod.GetParameters().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("width", startParams);
        Assert.Contains("height", startParams);
        Assert.Contains("username", startParams);
        Assert.Contains("password", startParams);
        Assert.Contains("pip", startParams);
        Assert.Contains("scale", startParams);
        Assert.Contains("position", startParams);
        Assert.Contains("interactive", startParams);
        Assert.Contains("margin", startParams);
        Assert.Contains("autoProvision", startParams);
        Assert.Contains("auto_provision", startParams);

        // 3. Verify computer_ghost_stop parameters
        var stopMethod = methods["computer_ghost_stop"];
        var stopParams = stopMethod.GetParameters().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("sessionId", stopParams);
        Assert.Contains("session_id", stopParams);
        Assert.Contains("stopPip", stopParams);
        Assert.Contains("stop_pip", stopParams);

        // 4. Verify computer_ghost_batch parameters
        var batchMethod = methods["computer_ghost_batch"];
        var batchParams = batchMethod.GetParameters().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("steps", batchParams);
        Assert.Contains("set", batchParams);
        Assert.Contains("until", batchParams);
        Assert.Contains("read", batchParams);
        Assert.Contains("detail", batchParams);
        Assert.Contains("failFast", batchParams);
        Assert.Contains("fail_fast", batchParams);
        Assert.Contains("autoDismissModals", batchParams);
        Assert.Contains("auto_dismiss_modals", batchParams);
        Assert.Contains("timeoutMs", batchParams);
        Assert.Contains("timeout_ms", batchParams);
        Assert.Contains("operationId", batchParams);
        Assert.Contains("operation_id", batchParams);

        // 5. Verify computer_ghost_control & ghost_control_open parameters
        foreach (var toolName in new[] { "computer_ghost_control", "ghost_control_open" })
        {
            var controlMethod = methods[toolName];
            var controlParams = controlMethod.GetParameters().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.Contains("action", controlParams);
            Assert.Contains("interactive", controlParams);
            Assert.Contains("fullScreen", controlParams);
            Assert.Contains("fullscreen", controlParams);
            Assert.Contains("full_screen", controlParams);
            Assert.Contains("position", controlParams);
            Assert.Contains("opacity", controlParams);
            Assert.Contains("scale", controlParams);
        }
    }

    [Fact]
    public async Task GhostMcpBridge_GetGhostStatus_ReturnsSessionIdPipActiveAndWindowHandle()
    {
        using var bridge = new GhostMcpBridge();
        var result = await bridge.GetGhostStatus();

        Assert.NotNull(result);
        Assert.False(result.IsError);
        Assert.NotEmpty(result.Content);

        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(t => t.Text));
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("success", out var successProp) && successProp.GetBoolean());
        Assert.True(root.TryGetProperty("sessionId", out _), "Missing sessionId in status response");
        Assert.True(root.TryGetProperty("pipActive", out var pipActiveProp) && (pipActiveProp.ValueKind == JsonValueKind.True || pipActiveProp.ValueKind == JsonValueKind.False), "Missing or non-boolean pipActive in status response");
        Assert.True(root.TryGetProperty("windowHandle", out _), "Missing windowHandle in status response");
    }

    [Fact]
    public async Task GhostMcpBridge_GhostControl_StatusReturnsSessionIdPipActiveAndWindowHandle()
    {
        using var bridge = new GhostMcpBridge();

        // Test ghost_control_open(action: "status")
        var openResult = await bridge.GhostControlOpen(action: "status");
        Assert.NotNull(openResult);
        var openText = string.Join("\n", openResult.Content.OfType<TextContentBlock>().Select(t => t.Text));
        using var openDoc = JsonDocument.Parse(openText);
        var openRoot = openDoc.RootElement;

        Assert.True(openRoot.TryGetProperty("success", out var openSuccess) && openSuccess.GetBoolean());
        Assert.True(openRoot.TryGetProperty("sessionId", out _), "Missing sessionId in ghost_control_open response");
        Assert.True(openRoot.TryGetProperty("pipActive", out var openPipActive) && (openPipActive.ValueKind == JsonValueKind.True || openPipActive.ValueKind == JsonValueKind.False), "Missing or non-boolean pipActive in ghost_control_open response");
        Assert.True(openRoot.TryGetProperty("windowHandle", out _), "Missing windowHandle in ghost_control_open response");

        // Test computer_ghost_control(action: "status")
        var ctrlResult = await bridge.ComputerGhostControl(action: "status");
        Assert.NotNull(ctrlResult);
        var ctrlText = string.Join("\n", ctrlResult.Content.OfType<TextContentBlock>().Select(t => t.Text));
        using var ctrlDoc = JsonDocument.Parse(ctrlText);
        var ctrlRoot = ctrlDoc.RootElement;

        Assert.True(ctrlRoot.TryGetProperty("success", out var ctrlSuccess) && ctrlSuccess.GetBoolean());
        Assert.True(ctrlRoot.TryGetProperty("sessionId", out _), "Missing sessionId in computer_ghost_control response");
        Assert.True(ctrlRoot.TryGetProperty("pipActive", out var ctrlPipActive) && (ctrlPipActive.ValueKind == JsonValueKind.True || ctrlPipActive.ValueKind == JsonValueKind.False), "Missing or non-boolean pipActive in computer_ghost_control response");
        Assert.True(ctrlRoot.TryGetProperty("windowHandle", out _), "Missing windowHandle in computer_ghost_control response");
    }

    [Fact]
    public async Task GhostMcpBridge_StopGhost_ReturnsSessionIdPipActiveAndWindowHandle()
    {
        using var bridge = new GhostMcpBridge();
        var result = await bridge.StopGhost(sessionId: 9999);

        Assert.NotNull(result);
        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(t => t.Text));
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("sessionId", out _), "Missing sessionId in StopGhost response");
        Assert.True(root.TryGetProperty("pipActive", out var pipActiveProp) && (pipActiveProp.ValueKind == JsonValueKind.True || pipActiveProp.ValueKind == JsonValueKind.False), "Missing or non-boolean pipActive in StopGhost response");
        Assert.True(root.TryGetProperty("windowHandle", out _), "Missing windowHandle in StopGhost response");
    }

    [Fact]
    public async Task GhostMcpBridge_GhostBatch_Validation()
    {
        using var bridge = new GhostMcpBridge();

        // 1. Both null/empty steps and set -> Malformed
        var r1 = await bridge.GhostBatch(steps: null, set: null);
        Assert.True(r1.IsError);
        var text1 = string.Join("\n", r1.Content.OfType<TextContentBlock>().Select(t => t.Text));
        Assert.Contains("Malformed", text1);

        // 2. Session inactive -> SessionInactive
        var r2 = await bridge.GhostBatch(steps: new[]
        {
            new InbriskTools.BatchStep(Action: "wait", Ms: 100)
        });
        Assert.True(r2.IsError);
        var text2 = string.Join("\n", r2.Content.OfType<TextContentBlock>().Select(t => t.Text));
        Assert.Contains("SessionInactive", text2);
    }

    [Fact]
    public async Task GhostMcpBridge_LocalDesktopBackend_CountsAsActiveGhostSession()
    {
        var localStatus = new GhostSessionStatus(
            IsActive: true,
            SessionId: null,
            Username: Environment.UserName,
            Uptime: TimeSpan.FromSeconds(3),
            State: "LocalDesktop",
            ProcessId: 1234);

        using var bridge = new GhostMcpBridge(
            sessionManager: new InactiveSessionManager(),
            localBackendStatusProvider: () => localStatus);

        var status = await bridge.GetSessionStatusAsync();
        Assert.True(status.IsActive);
        Assert.Equal("LocalDesktop", status.State);
        Assert.Equal(1234, status.ProcessId);
    }

    private sealed class InactiveSessionManager : IGhostSessionManager
    {
        public Task<GhostUserProvisionResult> EnsureProvisionedAsync(CancellationToken ct = default) =>
            Task.FromResult(GhostUserProvisionResult.Fail("InbriskAgent", "not provisioned in test"));

        public Task<GhostSessionStatus> StartSessionAsync(GhostSessionConfig? config = null, CancellationToken ct = default) =>
            Task.FromResult(GhostSessionStatus.Inactive);

        public Task<bool> StopSessionAsync(int? sessionId = null, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<GhostSessionStatus> GetStatusAsync(CancellationToken ct = default) =>
            Task.FromResult(GhostSessionStatus.Inactive);
    }
}
