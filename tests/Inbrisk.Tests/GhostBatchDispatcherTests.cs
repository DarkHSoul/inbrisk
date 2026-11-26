using System.Text.Json;
using Inbrisk.Core;
using Inbrisk.Runtime;

namespace Inbrisk.Tests;

[Trait("Category", "Contract")]
public sealed class GhostBatchDispatcherTests
{
    private sealed class FakeIpcBus : IGhostIpcBus
    {
        public bool IsConnected { get; set; } = true;
        public Func<string, string, Task<string>>? Handler { get; set; }
        public int CallCount { get; private set; }
        public string? LastAction { get; private set; }
        public string? LastPayload { get; private set; }

        public void RegisterHandler(string action, Func<string, Task<string>> handler) { }
        public Task<bool> PingAsync(TimeSpan timeout, CancellationToken ct = default) => Task.FromResult(IsConnected);

        public Task<string> SendRequestAsync(string action, string payloadJson, TimeSpan timeout, CancellationToken ct = default)
        {
            CallCount++;
            LastAction = action;
            LastPayload = payloadJson;
            if (Handler != null) return Handler(action, payloadJson);
            return Task.FromResult("""{"success":true,"stepsExecuted":1,"totalDurationMs":12,"results":[{"step":1,"do":"click","ok":true,"ms":12}]}""");
        }

        public Task<TResp?> SendRequestAsync<TReq, TResp>(string action, TReq payload, TimeSpan timeout, CancellationToken ct = default) =>
            Task.FromResult(default(TResp));

        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task DispatchBatchAsync_ValidPayload_SendsExecuteBatchAndParsesResult()
    {
        var bus = new FakeIpcBus();
        var dispatcher = new GhostBatchDispatcher(bus);

        var res = await dispatcher.DispatchBatchAsync("""{"steps":[{"do":"click","target":"OK"}]}""");

        Assert.True(res.Success);
        Assert.Equal(1, res.StepsExecuted);
        Assert.Equal(12, res.TotalDurationMs);
        Assert.Single(res.Results);
        Assert.Equal("click", res.Results[0].ActionName);
        Assert.Equal(GhostBatchDispatcher.ExecuteBatchAction, bus.LastAction);
    }

    [Fact]
    public void GhostBatchResult_Parse_HandlesMcpEnvelopeAndSnakeCaseAliases()
    {
        var inner = """{"ok":true,"steps_executed":2,"duration_ms":45,"results":[{"step":1,"action":"type","target":"Box","ok":true,"durationMs":20},{"step":2,"do":"click","t":"Save","ok":true,"ms":25}]}""";
        var mcpWrapped = JsonSerializer.Serialize(new { content = new[] { new { type = "text", text = inner } } });

        var parsed = GhostBatchResult.Parse(mcpWrapped);
        Assert.True(parsed.Success);
        Assert.Equal(2, parsed.StepsExecuted);
        Assert.Equal(45, parsed.TotalDurationMs);
        Assert.Equal(2, parsed.Results.Count);
        Assert.Equal("type", parsed.Results[0].ActionName);
        Assert.Equal("Box", parsed.Results[0].TargetName);
        Assert.Equal(20, parsed.Results[0].Duration);
        Assert.Equal("Save", parsed.Results[1].TargetName);
    }

    [Fact]
    public async Task DispatchBatchAsync_InvalidJsonOrExcessiveStepsOrPayload_FailsBeforeIpc()
    {
        var bus = new FakeIpcBus();
        var dispatcher = new GhostBatchDispatcher(bus);

        // Invalid JSON
        var badJson = await dispatcher.DispatchBatchAsync("{not valid json");
        Assert.False(badJson.Success);
        Assert.Contains("not valid JSON", badJson.Error);
        Assert.Equal(0, bus.CallCount);

        // > MaxBatchSteps (201 steps)
        var steps = Enumerable.Range(0, GhostBatchDispatcher.MaxBatchSteps + 1)
            .Select(i => new { @do = "wait", ms = 1 })
            .ToArray();
        var tooMany = await dispatcher.DispatchBatchAsync(new { steps });
        Assert.False(tooMany.Success);
        Assert.Contains($"{GhostBatchDispatcher.MaxBatchSteps}", tooMany.Error);
        Assert.Equal(0, bus.CallCount);

        // > MaxBatchPayloadChars
        var huge = "{\"steps\":[{\"text\":\"" + new string('a', GhostBatchDispatcher.MaxBatchPayloadChars) + "\"}]}";
        var tooBig = await dispatcher.DispatchBatchAsync(huge);
        Assert.False(tooBig.Success);
        Assert.Contains("limit", tooBig.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, bus.CallCount);
    }

    [Fact]
    public async Task DispatchBatchAsync_WhenDisconnectedOrConnectionLost_ThrowsGhostSessionNotReady()
    {
        var bus = new FakeIpcBus { IsConnected = false };
        var dispatcher = new GhostBatchDispatcher(bus);

        await Assert.ThrowsAsync<GhostSessionNotReadyException>(
            () => dispatcher.DispatchBatchAsync("""{"steps":[]}"""));

        bus.IsConnected = true;
        bus.Handler = (_, _) => throw new GhostIpcException("IPC connection was closed.");
        await Assert.ThrowsAsync<GhostSessionNotReadyException>(
            () => dispatcher.DispatchBatchAsync("""{"steps":[]}"""));
    }

    [Fact]
    public async Task DispatchBatchAsync_WhenEmergencyGateStopped_RefusesWithoutTouchingIpc()
    {
        var prevEnv = Environment.GetEnvironmentVariable("INBRISK_EMERGENCY_STATE");
        var tempMarker = Path.Combine(Path.GetTempPath(), $"inbrisk-emg-ghost-{Guid.NewGuid():N}.flag");
        try
        {
            Environment.SetEnvironmentVariable("INBRISK_EMERGENCY_STATE", tempMarker);
            File.WriteAllText(tempMarker, "stopped");

            var bus = new FakeIpcBus();
            var dispatcher = new GhostBatchDispatcher(bus);

            var res = await dispatcher.DispatchBatchAsync("""{"steps":[{"do":"click"}]}""");
            Assert.False(res.Success);
            Assert.Contains("emergency hotkey", res.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, bus.CallCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable("INBRISK_EMERGENCY_STATE", prevEnv);
            try { if (File.Exists(tempMarker)) File.Delete(tempMarker); } catch { }
        }
    }

    [Fact]
    public async Task DispatchBatchAsync_WhenIpcTimesOut_ThrowsTimeoutException()
    {
        var bus = new FakeIpcBus
        {
            Handler = (_, _) => throw new TimeoutException("inner timeout")
        };
        var dispatcher = new GhostBatchDispatcher(bus);

        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => dispatcher.DispatchBatchAsync("""{"steps":[]}""", TimeSpan.FromSeconds(2)));
        Assert.Contains("timed out", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
