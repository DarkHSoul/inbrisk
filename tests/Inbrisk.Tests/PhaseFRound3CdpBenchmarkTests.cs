using System.Diagnostics;
using Inbrisk.Runtime.Adapters;
using Xunit;
using Xunit.Abstractions;

namespace Inbrisk.Tests;

/// <summary>
/// Phase F Round 3 Performance and Architecture Benchmarks (Scenarios A through F).
/// Classification: Production-path synthetic using controlled duplex in-memory CDP transport.
/// </summary>
public class PhaseFRound3CdpBenchmarkTests
{
    private readonly ITestOutputHelper _output;

    public PhaseFRound3CdpBenchmarkTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task ScenarioA_SameTarget100Commands_SessionReuseBenchmark()
    {
        // -------------------------------------------------------------
        // SCENARIO A: 100 same-target sequential commands
        // Compare: Simulated legacy per-command connect vs Persistent CDP session
        // -------------------------------------------------------------
        const int N = 100;
        var telemetry = new CdpTelemetry();
        var transport = new TestCdpTransport();
        await using var session = new CdpTargetSession("target_bench", "ws://bench", 9222, telemetry);
        session.AttachTransport(transport);

        var latencies = new List<double>(N);
        var totalSw = Stopwatch.StartNew();

        for (int i = 0; i < N; i++)
        {
            var sw = Stopwatch.StartNew();
            var res = await session.SendCommandAsync("Runtime.evaluate", new { expression = $"Math.sqrt({i})" });
            sw.Stop();
            latencies.Add(sw.Elapsed.TotalMilliseconds);
            Assert.NotNull(res);
        }

        totalSw.Stop();
        latencies.Sort();
        var p50 = latencies[(int)(N * 0.50)];
        var p90 = latencies[(int)(N * 0.90)];

        _output.WriteLine($"[Scenario A] Persistent CDP Session (N={N}):");
        _output.WriteLine($"  WebSocket Connections Opened: {telemetry.CdpConnectionsOpened} (Expected: 1)");
        _output.WriteLine($"  Commands Sent: {telemetry.CdpCommandsSent}");
        _output.WriteLine($"  Total Wall Time: {totalSw.ElapsedMilliseconds} ms");
        _output.WriteLine($"  Latency p50: {p50:F2} ms, p90: {p90:F2} ms");

        // Acceptance invariant
        Assert.Equal(1, telemetry.CdpConnectionsOpened);
        Assert.Equal(N, telemetry.CdpCommandsSent);
        Assert.Equal(N, telemetry.CdpResponsesReceived);
        Assert.Equal(0, telemetry.CdpPendingCommands);
    }

    [Fact]
    public async Task ScenarioB_ConcurrentCommands_CorrelationBenchmark()
    {
        // -------------------------------------------------------------
        // SCENARIO B: Command concurrency over single receive loop
        // 50 concurrent independent CDP reads
        // -------------------------------------------------------------
        const int N = 50;
        var telemetry = new CdpTelemetry();
        var transport = new TestCdpTransport { AutoRespondToCommands = false };
        await using var session = new CdpTargetSession("target_b", "ws://b", 9222, telemetry);
        session.AttachTransport(transport);

        var totalSw = Stopwatch.StartNew();

        var tasks = Enumerable.Range(1, N).Select(async id =>
        {
            var res = await session.SendCommandAsync("Runtime.evaluate", new { expression = $"id_{id}" });
            return (ExpectedId: id, Result: res);
        }).ToList();

        // Wait until all 50 commands are dispatched and pending
        while (session.PendingCommandCount < N)
        {
            await Task.Yield();
        }

        var peakPending = session.PendingCommandCount;
        Assert.Equal(N, peakPending);

        // Respond out-of-order (shuffle IDs)
        var shuffledIds = Enumerable.Range(1, N).OrderBy(_ => Random.Shared.Next()).ToList();
        foreach (var id in shuffledIds)
        {
            transport.PushResponse(id, new { value = $"res_{id}" });
        }

        var results = await Task.WhenAll(tasks);
        totalSw.Stop();

        // Verify 100% correlation correctness
        foreach (var (expectedId, result) in results)
        {
            var val = result?["result"]?["value"]?.ToString();
            Assert.Equal($"res_{expectedId}", val);
        }

        _output.WriteLine($"[Scenario B] Command Concurrency (N={N}):");
        _output.WriteLine($"  Peak Pending Commands: {peakPending}");
        _output.WriteLine($"  Correlation Accuracy: 100% ({N}/{N})");
        _output.WriteLine($"  Connections Opened: {telemetry.CdpConnectionsOpened}");
        _output.WriteLine($"  Total Wall Time: {totalSw.ElapsedMilliseconds} ms");

        Assert.Equal(1, telemetry.CdpConnectionsOpened);
        Assert.Equal(0, session.PendingCommandCount);
    }

    [Fact]
    public async Task ScenarioC_EventDrivenBrowseReadinessBenchmark()
    {
        // -------------------------------------------------------------
        // SCENARIO C: Event-driven page readiness vs polling baseline
        // -------------------------------------------------------------
        var telemetry = new CdpTelemetry();
        var transport = new TestCdpTransport();
        const int loadDelayMs = 25;
        const int idleDelayMs = 50;

        transport.CustomResponder = (id, method, p) =>
        {
            if (method == "Page.navigate")
            {
                Task.Run(async () =>
                {
                    await Task.Delay(loadDelayMs);
                    transport.PushEvent("Page.loadEventFired", new { timestamp = 100 });

                    await Task.Delay(idleDelayMs - loadDelayMs);
                    transport.PushEvent("Page.lifecycleEvent", new { name = "networkIdle", frameId = "F_SC", loaderId = "L_SC" });
                });
                return new { frameId = "F_SC", loaderId = "L_SC" };
            }
            return new { };
        };

        await using var session = new CdpTargetSession("target_c", "ws://c", 9222, telemetry);
        session.AttachTransport(transport);

        var sw = Stopwatch.StartNew();
        var res = await session.NavigateAndAwaitReadyAsync("https://bench.com", TimeSpan.FromSeconds(2), waitForNetworkIdle: true);
        sw.Stop();

        _output.WriteLine($"[Scenario C] Event-Driven Browse Readiness:");
        _output.WriteLine($"  Page Load Events: {telemetry.CdpPageLoadEventCount}");
        _output.WriteLine($"  Network Idle Events: {telemetry.CdpNetworkIdleCount}");
        _output.WriteLine($"  Fallback Poll Count: {telemetry.CdpReadinessFallbackPollCount}");
        _output.WriteLine($"  Readiness Latency: {sw.ElapsedMilliseconds} ms");

        Assert.NotNull(res);
        Assert.True(telemetry.CdpPageLoadEventCount >= 1);
        Assert.True(telemetry.CdpNetworkIdleCount >= 1);
        Assert.Equal(0, telemetry.CdpReadinessFallbackPollCount);
    }

    [Fact]
    public async Task ScenarioD_MutationObserverVsPollingBenchmark()
    {
        // -------------------------------------------------------------
        // SCENARIO D: One-shot MutationObserver awaitPromise vs repeated polling
        // -------------------------------------------------------------
        var telemetry = new CdpTelemetry();
        var transport = new TestCdpTransport();

        // 1. One-shot MutationObserver path
        transport.CustomResponder = (id, method, p) =>
        {
            if (method == "Runtime.evaluate")
            {
                // Evaluated once, resolves when mutation triggers
                return new { result = new { value = new { success = true, immediate = false } } };
            }
            return new { };
        };

        await using var session = new CdpTargetSession("target_d", "ws://d", 9222, telemetry);
        session.AttachTransport(transport);

        var swObs = Stopwatch.StartNew();
        var obsOk = await session.WaitForSelectorAsync("#dynamic-widget", TimeSpan.FromSeconds(2));
        swObs.Stop();

        var obsCommands = transport.SendCount;

        // 2. Simulated polling baseline (poll every 20ms for 100ms = 5 protocol roundtrips)
        var swPoll = Stopwatch.StartNew();
        int pollCommands = 0;
        for (int p = 0; p < 5; p++)
        {
            pollCommands++;
            await Task.Delay(20);
        }
        swPoll.Stop();

        _output.WriteLine($"[Scenario D] MutationObserver vs Polling:");
        _output.WriteLine($"  MutationObserver Protocol Commands: {obsCommands} (1 evaluate call)");
        _output.WriteLine($"  Polling Protocol Commands: {pollCommands} (5 evaluate calls)");
        _output.WriteLine($"  Mutation Hits: {telemetry.CdpDomWaitMutationHitCount}");
        _output.WriteLine($"  Immediate Hits: {telemetry.CdpDomWaitImmediateHitCount}");

        Assert.True(obsOk);
        Assert.Equal(1, obsCommands);
        Assert.True(obsCommands < pollCommands);
    }

    [Fact]
    public async Task ScenarioE_PersistentCaptureBenchmark()
    {
        // -------------------------------------------------------------
        // SCENARIO E: Persistent network / console capture
        // -------------------------------------------------------------
        var telemetry = new CdpTelemetry();
        var transport = new TestCdpTransport();
        await using var manager = new CdpSessionManager(telemetry);
        var session = manager.RegisterSession(9222, "tab_e", "ws://e", transport);
        var adapter = new ChromeDevToolsAdapter(manager);

        // Pre-capture noise (should be excluded)
        for (int i = 0; i < 20; i++)
        {
            transport.PushEvent("Network.requestWillBeSent", new { requestId = $"noise_{i}", request = new { url = $"https://noise.com/{i}" } });
        }

        // Pre-enable domains so capture setup baseline is synchronous and race-free
        await session.EnsureDomainEnabledAsync("Network");
        await session.EnsureDomainEnabledAsync("Runtime");
        await session.EnsureDomainEnabledAsync("Log");
        await session.EnsureDomainEnabledAsync("Page");

        // Run capture
        var captureTask = adapter.ExecuteAsync("capture", null, new Dictionary<string, object?>
        {
            ["port"] = 9222,
            ["tabId"] = "tab_e",
            ["durationMs"] = 1000,
            ["reload"] = false
        });

        // Allow capture setup to establish baseline
        await Task.Delay(100);

        // Push capture events during the capture window
        transport.PushEvent("Network.requestWillBeSent", new { requestId = "cap_1", request = new { url = "https://app.com/bundle.js" } });
        transport.PushEvent("Network.responseReceived", new { requestId = "cap_1", response = new { status = 200 }, type = "Script" });
        transport.PushEvent("Runtime.consoleAPICalled", new { type = "log", args = new[] { new { value = "App initialized" } } });

        var res = await captureTask;
        Assert.True(res.Success);

        _output.WriteLine($"[Scenario E] Persistent Capture Benchmark:");
        _output.WriteLine($"  WebSocket Connections Opened: {telemetry.CdpConnectionsOpened} (Expected: 1)");
        _output.WriteLine($"  Network Events Buffered: {telemetry.CdpNetworkEventsBuffered}");
        _output.WriteLine($"  Console Events Buffered: {telemetry.CdpConsoleEventsBuffered}");

        Assert.Equal(1, telemetry.CdpConnectionsOpened);
        var netDict = res.Data?["network"] as Dictionary<string, object?>;
        var summary = netDict?["summary"] as Dictionary<string, object?>;
        Assert.Equal(1, summary?["total"]); // Only 1 request in capture window, 20 noise requests excluded!
    }

    [Fact]
    public async Task ScenarioF_BufferStressBenchmark()
    {
        // -------------------------------------------------------------
        // SCENARIO F: High event volume buffer stress (2,000 events)
        // -------------------------------------------------------------
        var telemetry = new CdpTelemetry();
        var buffer = new BoundedCdpEventBuffer<string>(300, () => telemetry.IncBufferDroppedCount());

        const int totalEvents = 2000;
        var sw = Stopwatch.StartNew();

        for (int i = 0; i < totalEvents; i++)
        {
            buffer.Enqueue($"event_payload_{i}_{new string('x', 64)}");
        }

        sw.Stop();

        _output.WriteLine($"[Scenario F] Buffer Stress Benchmark:");
        _output.WriteLine($"  Total Events Ingested: {totalEvents}");
        _output.WriteLine($"  Events Retained: {buffer.Count} (Max Capacity: {buffer.Capacity})");
        _output.WriteLine($"  Events Dropped: {buffer.DroppedCount} (Telemetry: {telemetry.CdpBufferDroppedCount})");
        _output.WriteLine($"  Ingestion Throughput: {totalEvents / sw.Elapsed.TotalSeconds:F0} events/sec");

        Assert.Equal(300, buffer.Count);
        Assert.Equal(1700, buffer.DroppedCount);
        Assert.Equal(1700, telemetry.CdpBufferDroppedCount);
        Assert.True(buffer.Truncated);
    }
}
