using Inbrisk.Core;

namespace Inbrisk.Tests;

[Trait("Category", "Contract")]
public sealed class GhostIpcBusTests
{
    private sealed record EchoReq(string Text, int Seq);
    private sealed record EchoResp(string Echoed, int Seq);

    [Fact]
    public async Task ServerAndClient_BidirectionalRequestsTypedAndPing_WorkOverNamedPipe()
    {
        var pipeName = $"inbrisk_ghost_test_{Guid.NewGuid():N}";
        await using var server = new GhostIpcServer(pipeName);
        await using var client = new GhostIpcClient(pipeName);

        var connectedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ClientConnected += (_, _) => connectedTcs.TrySetResult();

        server.RegisterHandler("host_echo", payload => Task.FromResult($"host:{payload}"));
        client.RegisterHandler("worker_echo", async payload =>
        {
            var req = System.Text.Json.JsonSerializer.Deserialize<EchoReq>(payload,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            return System.Text.Json.JsonSerializer.Serialize(new EchoResp(req.Text.ToUpperInvariant(), req.Seq));
        });

        await server.StartAsync();
        await client.ConnectAsync(TimeSpan.FromSeconds(5));
        await connectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(server.IsConnected);
        Assert.True(client.IsConnected);

        // Ping both ways
        Assert.True(await server.PingAsync(TimeSpan.FromSeconds(3)));
        Assert.True(await client.PingAsync(TimeSpan.FromSeconds(3)));

        // Client -> Server
        var fromHost = await client.SendRequestAsync("host_echo", "ping-data", TimeSpan.FromSeconds(3));
        Assert.Equal("host:ping-data", fromHost);

        // Server -> Client (typed)
        var typed = await server.SendRequestAsync<EchoReq, EchoResp>(
            "worker_echo", new EchoReq("hello", 7), TimeSpan.FromSeconds(3));
        Assert.NotNull(typed);
        Assert.Equal("HELLO", typed!.Echoed);
        Assert.Equal(7, typed.Seq);
    }

    [Fact]
    public async Task ConcurrentRequests_CorrelateCorrectlyUnderLoad()
    {
        var pipeName = $"inbrisk_ghost_conc_{Guid.NewGuid():N}";
        await using var server = new GhostIpcServer(pipeName);
        await using var client = new GhostIpcClient(pipeName);

        var connectedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ClientConnected += (_, _) => connectedTcs.TrySetResult();

        client.RegisterHandler("add_one", async payload =>
        {
            var n = int.Parse(payload);
            await Task.Delay((n % 5) * 3); // out-of-order completion
            return (n + 1).ToString();
        });

        await server.StartAsync();
        await client.ConnectAsync(TimeSpan.FromSeconds(5));
        await connectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var tasks = Enumerable.Range(0, 20)
            .Select(i => server.SendRequestAsync("add_one", i.ToString(), TimeSpan.FromSeconds(5)))
            .ToArray();

        var results = await Task.WhenAll(tasks);
        for (int i = 0; i < 20; i++)
            Assert.Equal((i + 1).ToString(), results[i]);
    }

    [Fact]
    public async Task UnregisteredActionAndHandlerException_ReturnGhostIpcExceptionWithoutDroppingBus()
    {
        var pipeName = $"inbrisk_ghost_err_{Guid.NewGuid():N}";
        await using var server = new GhostIpcServer(pipeName);
        await using var client = new GhostIpcClient(pipeName);

        var connectedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ClientConnected += (_, _) => connectedTcs.TrySetResult();

        client.RegisterHandler("boom", _ => throw new InvalidOperationException("intentional worker failure"));
        client.RegisterHandler("ok", payload => Task.FromResult("still-alive:" + payload));

        await server.StartAsync();
        await client.ConnectAsync(TimeSpan.FromSeconds(5));
        await connectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var missingEx = await Assert.ThrowsAsync<GhostIpcException>(
            () => server.SendRequestAsync("does_not_exist", "{}", TimeSpan.FromSeconds(3)));
        Assert.Contains("No handler registered", missingEx.Message);

        var boomEx = await Assert.ThrowsAsync<GhostIpcException>(
            () => server.SendRequestAsync("boom", "{}", TimeSpan.FromSeconds(3)));
        Assert.Contains("intentional worker failure", boomEx.Message);

        // Bus must remain healthy after handler errors
        var healthy = await server.SendRequestAsync("ok", "42", TimeSpan.FromSeconds(3));
        Assert.Equal("still-alive:42", healthy);
    }

    [Fact]
    public async Task ClientDisconnect_FaultsInFlightRequestImmediately()
    {
        var pipeName = $"inbrisk_ghost_disc_{Guid.NewGuid():N}";
        await using var server = new GhostIpcServer(pipeName);
        var client = new GhostIpcClient(pipeName);

        var connectedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnectedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ClientConnected += (_, _) => connectedTcs.TrySetResult();
        server.ClientDisconnected += (_, _) => disconnectedTcs.TrySetResult();

        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.RegisterHandler("hang", async _ =>
        {
            handlerStarted.TrySetResult();
            await Task.Delay(10_000);
            return "never";
        });

        await server.StartAsync();
        await client.ConnectAsync(TimeSpan.FromSeconds(5));
        await connectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var inFlight = server.SendRequestAsync("hang", "{}", TimeSpan.FromSeconds(10));
        await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        // Drop client connection abruptly
        await client.DisposeAsync();

        await Assert.ThrowsAsync<GhostIpcException>(() => inFlight.WaitAsync(TimeSpan.FromSeconds(5)));
        await disconnectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(server.IsConnected);
    }

    [Fact]
    public void ResolveAccountSid_NonexistentAccount_ReturnsNullFailClosed()
    {
        Assert.Null(GhostIpcDefaults.ResolveAccountSid(null));
        Assert.Null(GhostIpcDefaults.ResolveAccountSid("   "));
        Assert.Null(GhostIpcDefaults.ResolveAccountSid("NoSuchAccount_" + Guid.NewGuid().ToString("N")));
    }
}
