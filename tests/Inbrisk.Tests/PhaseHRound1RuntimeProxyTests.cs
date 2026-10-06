using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using Inbrisk.Core;
using Inbrisk.Mcp;
using Inbrisk.Mcp.Daemon;
using Inbrisk.Runtime;
using Xunit;

namespace Inbrisk.Tests;

/// <summary>
/// Phase H Round 1: Persistent Runtime Daemon + Thin Stdio Proxy Tests.
/// Validates:
/// 1. Two proxies reuse a single daemon instance.
/// 2. Concurrent startup produces exactly one daemon.
/// 3. Logical sessions remain isolated per proxy connection.
/// 4. All sessions share the daemon's authoritative single DesktopArbiter.
/// 5. Disconnecting one proxy disposes its session without terminating the daemon or other sessions.
/// 6. Crash / restart invalidates old session handles and reconnects with a new epoch.
/// 7. In-flight mutating requests fail truthfully on disconnect and are NOT silently replayed.
/// 8. Protocol version mismatch fails clearly during handshake.
/// 9. Stdio output is strictly JSON-RPC protocol clean (logs go to stderr).
/// 10. Clean daemon shutdown disposes all shared resources.
/// </summary>
public sealed class PhaseHRound1RuntimeProxyTests
{
    [Fact]
    public async Task Runtime_TwoProxiesReuseSingleDaemon()
    {
        var pipeName = $"inbrisk-test-reuse-{Guid.NewGuid():N}";
        var mutexName = $@"Local\inbrisk-mutex-reuse-{Guid.NewGuid():N}";

        await using var daemon = new InbriskRuntimeDaemon(pipeName, mutexName);
        await daemon.StartAsync();

        await using var proxy1 = new ThinStdioProxy(pipeName, mutexName);
        await proxy1.ConnectAsync();

        await using var proxy2 = new ThinStdioProxy(pipeName, mutexName);
        await proxy2.ConnectAsync();

        Assert.True(proxy1.IsConnected);
        Assert.True(proxy2.IsConnected);
        Assert.Equal(2, daemon.ConnectedSessionCount);
        Assert.NotEqual(proxy1.SessionId, proxy2.SessionId);

        var s1 = daemon.GetSession(proxy1.SessionId!);
        var s2 = daemon.GetSession(proxy2.SessionId!);
        Assert.NotNull(s1);
        Assert.NotNull(s2);
        Assert.Same(daemon.Arbiter, s1!.Arbiter);
        Assert.Same(daemon.Arbiter, s2!.Arbiter);
    }

    [Fact]
    public async Task Runtime_ConcurrentStartupCreatesSingleDaemon()
    {
        var pipeName = $"inbrisk-test-concurrent-{Guid.NewGuid():N}";
        var mutexName = $@"Local\inbrisk-mutex-concurrent-{Guid.NewGuid():N}";

        int daemonsCreated = 0;
        InbriskRuntimeDaemon? createdDaemon = null;

        async Task<InbriskRuntimeDaemon> DaemonFactory()
        {
            var d = new InbriskRuntimeDaemon(pipeName, mutexName);
            await d.StartAsync();
            Interlocked.Increment(ref daemonsCreated);
            createdDaemon = d;
            return d;
        }

        var tasks = Enumerable.Range(0, 5).Select(async _ =>
        {
            var proxy = new ThinStdioProxy(pipeName, mutexName);
            await proxy.ConnectOrStartDaemonAsync(DaemonFactory);
            return proxy;
        }).ToArray();

        var proxies = await Task.WhenAll(tasks);

        try
        {
            Assert.Equal(1, daemonsCreated);
            Assert.NotNull(createdDaemon);
            Assert.Equal(5, createdDaemon!.ConnectedSessionCount);
            foreach (var p in proxies)
            {
                Assert.True(p.IsConnected);
            }
        }
        finally
        {
            foreach (var p in proxies)
            {
                await p.DisposeAsync();
            }
            if (createdDaemon != null)
            {
                await createdDaemon.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task Runtime_LogicalSessionsRemainIsolated()
    {
        var pipeName = $"inbrisk-test-iso-{Guid.NewGuid():N}";
        await using var daemon = new InbriskRuntimeDaemon(pipeName);
        await daemon.StartAsync();

        await using var proxy1 = new ThinStdioProxy(pipeName);
        await proxy1.ConnectAsync();

        await using var proxy2 = new ThinStdioProxy(pipeName);
        await proxy2.ConnectAsync();

        var s1 = daemon.GetSession(proxy1.SessionId!);
        var s2 = daemon.GetSession(proxy2.SessionId!);

        Assert.NotNull(s1);
        Assert.NotNull(s2);
        Assert.NotSame(s1, s2);
        Assert.NotEqual(s1!.SessionId, s2!.SessionId);

        // Isolated cache instances
        Assert.NotSame(s1.FindCache, s2.FindCache);
        Assert.NotSame(s1.SnapshotCache, s2.SnapshotCache);
        Assert.NotSame(s1.Deduplicator, s2.Deduplicator);
        Assert.NotSame(s1.Telemetry, s2.Telemetry);
        Assert.NotSame(s1.SessionCts, s2.SessionCts);

        // Mutating s1 FindCache does not leak to s2
        var cacheKey = new FindCacheKey(null, null, null, null, "calc", null, null, null, null, null, null, true, false);
        await s1.FindCache.GetOrComputeAsync(cacheKey, _ => Task.FromResult<IReadOnlyList<UiElement>>(new[]
        {
            new UiElement("e1", BackendId.Uia, Role.Window, "calc", new RectPx(0, 0, 100, 100), Array.Empty<string>(), new Dictionary<string, object?>(), new ElementHandle(BackendId.Uia, "e1", new ReResolveRecipe(1, 1, "calc", Role.Window, "calc", "calc", Array.Empty<AncestryStep>(), new RectPx(0, 0, 100, 100))), 1, 1)
        }));

        Assert.True(s1.FindCache.EntryCount > 0);
        Assert.Equal(0, s2.FindCache.EntryCount);

        // Canceling s1 SessionCts does not cancel s2
        s1.SessionCts.Cancel();
        Assert.True(s1.SessionCts.IsCancellationRequested);
        Assert.False(s2.SessionCts.IsCancellationRequested);
    }

    [Fact]
    public async Task Runtime_SessionsShareSingleDesktopArbiter()
    {
        var pipeName = $"inbrisk-test-arbiter-{Guid.NewGuid():N}";
        await using var daemon = new InbriskRuntimeDaemon(pipeName);
        await daemon.StartAsync();

        await using var proxy1 = new ThinStdioProxy(pipeName);
        await proxy1.ConnectAsync();

        await using var proxy2 = new ThinStdioProxy(pipeName);
        await proxy2.ConnectAsync();

        var s1 = daemon.GetSession(proxy1.SessionId!);
        var s2 = daemon.GetSession(proxy2.SessionId!);

        Assert.NotNull(s1);
        Assert.NotNull(s2);
        Assert.Same(daemon.Arbiter, s1!.Arbiter);
        Assert.Same(daemon.Arbiter, s2!.Arbiter);

        // Session 1 acquires exclusive physical lease
        await using var lease1 = await s1.Arbiter.AcquireAsync(
            s1.SessionId,
            LeaseKind.PhysicalInput,
            "Session 1 physical typing");

        Assert.True(lease1.IsActive);
        Assert.Equal(s1.SessionId, daemon.Arbiter.GetExclusiveOwner()?.OwnerId);

        // Session 2 attempts to acquire physical lease with short timeout -> must time out
        await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await s2.Arbiter.AcquireAsync(
                s2.SessionId,
                LeaseKind.PhysicalInput,
                "Session 2 physical typing",
                timeout: TimeSpan.FromMilliseconds(50));
        });

        // Release lease 1
        lease1.Release();
        Assert.Null(daemon.Arbiter.GetExclusiveOwner());

        // Session 2 can now acquire the lease
        await using var lease2 = await s2.Arbiter.AcquireAsync(
            s2.SessionId,
            LeaseKind.PhysicalInput,
            "Session 2 physical typing",
            timeout: TimeSpan.FromMilliseconds(500));

        Assert.True(lease2.IsActive);
        Assert.Equal(s2.SessionId, daemon.Arbiter.GetExclusiveOwner()?.OwnerId);
    }

    [Fact]
    public async Task Runtime_ProxyDisconnectDoesNotKillOtherSessions()
    {
        var pipeName = $"inbrisk-test-disconnect-{Guid.NewGuid():N}";
        await using var daemon = new InbriskRuntimeDaemon(pipeName);
        await daemon.StartAsync();

        await using var proxy1 = new ThinStdioProxy(pipeName);
        await proxy1.ConnectAsync();

        await using var proxy2 = new ThinStdioProxy(pipeName);
        await proxy2.ConnectAsync();

        Assert.Equal(2, daemon.ConnectedSessionCount);
        var s1 = daemon.GetSession(proxy1.SessionId!);
        var s2 = daemon.GetSession(proxy2.SessionId!);
        Assert.NotNull(s1);
        Assert.NotNull(s2);
        Assert.False(s1!.IsDisposed);
        Assert.False(s2!.IsDisposed);

        // Disconnect proxy 1
        await proxy1.DisconnectAsync();

        // Give daemon listener/stream loop a brief moment to process disconnect
        await Task.Delay(150);

        // Session 1 disposed, daemon still running, Session 2 alive and functional
        Assert.True(s1.IsDisposed);
        Assert.True(daemon.IsRunning);
        Assert.False(s2.IsDisposed);
        Assert.Equal(1, daemon.ConnectedSessionCount);
        Assert.Null(s2.Arbiter.GetExclusiveOwner());
    }

    [Fact]
    public async Task Runtime_CrashInvalidatesOldHandlesAndReconnects()
    {
        var pipeName = $"inbrisk-test-reconnect-{Guid.NewGuid():N}";
        var daemon1 = new InbriskRuntimeDaemon(pipeName, epoch: 1);
        await daemon1.StartAsync();

        await using var proxy = new ThinStdioProxy(pipeName);
        await proxy.ConnectAsync();

        var initialSessionId = proxy.SessionId;
        var initialEpoch = proxy.SessionEpoch;
        Assert.Equal(1, initialEpoch);

        // Handle registered in epoch 1
        var handleEpoch1 = proxy.RegisterHandle("window-handle-xyz");
        Assert.True(proxy.IsValidHandle(handleEpoch1));
        Assert.True(proxy.IsValidHandle("window-handle-xyz"));

        // Simulate crash of daemon 1
        await daemon1.DisposeAsync();

        // Restart daemon with epoch 2
        await using var daemon2 = new InbriskRuntimeDaemon(pipeName, epoch: 2);
        await daemon2.StartAsync();

        // Proxy reconnects
        await proxy.ReconnectAsync();

        Assert.NotEqual(initialSessionId, proxy.SessionId);
        Assert.Equal(2, proxy.SessionEpoch);

        // Old handle from epoch 1 is invalidated
        Assert.False(proxy.IsValidHandle(handleEpoch1));
        Assert.False(proxy.IsValidHandle("window-handle-xyz"));
    }

    [Fact]
    public async Task Runtime_MutationNotSilentlyReplayedAfterDisconnect()
    {
        var pipeName = $"inbrisk-test-nomutationreplay-{Guid.NewGuid():N}";
        var daemon1 = new InbriskRuntimeDaemon(pipeName, epoch: 1);
        await daemon1.StartAsync();

        await using var proxy = new ThinStdioProxy(pipeName);
        await proxy.ConnectAsync();

        var mutatingJsonRpc = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 101,
            method = "tools/call",
            @params = new
            {
                name = "computer_click",
                arguments = new { x = 100, y = 100 }
            }
        });

        // Start sending mutating request, and immediately kill daemon1 while in-flight
        var requestTask = proxy.SendRawRequestAsync(mutatingJsonRpc);

        // Abruptly sever the connection
        await daemon1.DisposeAsync();

        // Mutating in-flight request MUST fail truthfully with InFlightMutationFailedException
        var ex = await Assert.ThrowsAsync<InFlightMutationFailedException>(async () => await requestTask);
        Assert.Contains("mutating", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Restart daemon
        await using var daemon2 = new InbriskRuntimeDaemon(pipeName, epoch: 2);
        await daemon2.StartAsync();

        // Reconnect proxy
        await proxy.ReconnectAsync();

        // Mutating request was NOT silently replayed
        Assert.Empty(proxy.InFlightRequests);
        var s2 = daemon2.GetSession(proxy.SessionId!);
        Assert.NotNull(s2);
    }

    [Fact]
    public async Task Runtime_ProtocolVersionMismatchFailsClearly()
    {
        var pipeName = $"inbrisk-test-ver-mismatch-{Guid.NewGuid():N}";
        await using var daemon = new InbriskRuntimeDaemon(pipeName);
        await daemon.StartAsync();

        await using var proxy = new ThinStdioProxy(pipeName);
        proxy.ClientProtocolVersion = 999;

        var ex = await Assert.ThrowsAsync<ProtocolVersionMismatchException>(async () =>
        {
            await proxy.ConnectAsync();
        });

        Assert.Contains("Protocol version mismatch", ex.Message);
        Assert.Contains("999", ex.Message);
        Assert.Contains("1", ex.Message);
    }

    [Fact]
    public async Task Runtime_StdioRemainsProtocolClean()
    {
        var pipeName = $"inbrisk-test-stdio-clean-{Guid.NewGuid():N}";
        await using var daemon = new InbriskRuntimeDaemon(pipeName);
        await daemon.StartAsync();

        var stdinText = string.Join("\n", new[]
        {
            JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "test", version = "1.0" } } }),
            JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "notifications/initialized" }),
            JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 2, method = "tools/list", @params = new { } })
        }) + "\n";

        var stdin = new StringReader(stdinText);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        await using var proxy = new ThinStdioProxy(pipeName);
        await proxy.RunStdioAsync(stdin, stdout, stderr, maxFrames: 2);

        var stdoutOutput = stdout.ToString();
        var lines = stdoutOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.NotEmpty(lines);

        foreach (var line in lines)
        {
            // Every single line emitted to stdout MUST strictly parse as valid JSON-RPC
            using var doc = JsonDocument.Parse(line);
            Assert.Equal("2.0", doc.RootElement.GetProperty("jsonrpc").GetString());
        }
    }

    [Fact]
    public async Task Runtime_ShutdownDisposesSharedResources()
    {
        var pipeName = $"inbrisk-test-shutdown-{Guid.NewGuid():N}";
        var daemon = new InbriskRuntimeDaemon(pipeName);
        await daemon.StartAsync();

        await using var proxy = new ThinStdioProxy(pipeName);
        await proxy.ConnectAsync();

        var session = daemon.GetSession(proxy.SessionId!);
        var arbiter = daemon.Arbiter;

        Assert.NotNull(session);
        Assert.False(session!.IsDisposed);
        Assert.False(arbiter.IsDisposed);

        // Clean shutdown
        await daemon.DisposeAsync();

        Assert.True(daemon.IsDisposed);
        Assert.True(arbiter.IsDisposed);
        Assert.True(session.IsDisposed);

        // Arbiter throws ObjectDisposedException
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
        {
            await arbiter.AcquireAsync("test", LeaseKind.PhysicalInput, "test");
        });

        // Named pipe server is closed
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
            await client.ConnectAsync(50);
        });
    }
}
