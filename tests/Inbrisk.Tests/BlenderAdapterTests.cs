using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Inbrisk.Core;
using Inbrisk.Runtime.Adapters;

namespace Inbrisk.Tests;

[Trait("Category", "Contract")]
public sealed class BlenderAdapterTests
{
    [Theory]
    [InlineData("blender", true)]
    [InlineData("blender.exe", true)]
    [InlineData("Blender-4.2.exe", true)]
    [InlineData("notepad.exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsApplicable_MatchesBlenderProcessesOnly(string? procName, bool expected)
    {
        var adapter = new BlenderAdapter();
        Assert.Equal(expected, adapter.IsApplicable(procName, null));
    }

    [Theory]
    [InlineData("execute", true)]
    [InlineData("exec", true)]
    [InlineData("eval", true)]
    [InlineData("evaluate", true)]
    [InlineData("status", true)]
    [InlineData("ping", true)]
    [InlineData("get_scene", true)]
    [InlineData("scene", true)]
    [InlineData("bootstrap", true)]
    [InlineData("auto_install", true)]
    [InlineData("install", true)]
    [InlineData("delete_all", false)]
    public void CanHandle_RecognizesSupportedActionsAndAliases(string action, bool expected)
    {
        var adapter = new BlenderAdapter();
        Assert.Equal(expected, adapter.CanHandle(action));
    }

    [Fact]
    public async Task ExecuteAsync_MissingCodeOrUnknownAction_ReturnsUnsupported()
    {
        var adapter = new BlenderAdapter();

        var execNoCode = await adapter.ExecuteAsync("execute");
        Assert.False(execNoCode.Success);
        Assert.Equal(ErrorCode.Unsupported, execNoCode.Error);

        var evalNoCode = await adapter.ExecuteAsync("eval");
        Assert.False(evalNoCode.Success);
        Assert.Equal(ErrorCode.Unsupported, evalNoCode.Error);

        var unknown = await adapter.ExecuteAsync("bogus_action");
        Assert.False(unknown.Success);
        Assert.Equal(ErrorCode.Unsupported, unknown.Error);
    }

    [Fact]
    public async Task ExecuteAsync_OverLoopbackBridge_SerializesProtocolAndParsesResponses()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var receivedRequests = new List<JsonNode>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var serverTask = Task.Run(async () =>
        {
            for (int i = 0; i < 4; i++)
            {
                using var client = await listener.AcceptTcpClientAsync(cts.Token);
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var line = await reader.ReadLineAsync(cts.Token);
                var req = JsonNode.Parse(line!)!;
                receivedRequests.Add(req);

                var type = req["type"]?.GetValue<string>();
                string resp = type switch
                {
                    "ping" => """{"ok":true,"id":"r1","result":{"blender":"4.2.0","scene":"Scene","objects":3}}""",
                    "execute" => """{"ok":true,"id":"r2","result":42,"stdout":"done\n"}""",
                    "eval" when req["code"]!.GetValue<string>() == "1+1" => """{"ok":true,"id":"r3","result":2}""",
                    "eval" => """{"ok":false,"error":"NameError: name 'foo' is not defined"}""",
                    _ => """{"ok":false,"error":"unexpected"}"""
                };

                var bytes = Encoding.UTF8.GetBytes(resp + "\n");
                await stream.WriteAsync(bytes, cts.Token);
                await stream.FlushAsync(cts.Token);
            }
        }, cts.Token);

        var adapter = new BlenderAdapter();

        var status = await adapter.ExecuteAsync("status", args: new Dictionary<string, object?> { ["port"] = port });
        Assert.True(status.Success, status.Detail);
        Assert.Contains("4.2.0", status.Detail);

        var exec = await adapter.ExecuteAsync("execute", args: new Dictionary<string, object?>
        {
            ["port"] = port,
            ["code"] = "result = 42"
        });
        Assert.True(exec.Success, exec.Detail);
        Assert.Equal("done\n", exec.Data?["stdout"]);

        var evalOk = await adapter.ExecuteAsync("eval", args: new Dictionary<string, object?>
        {
            ["port"] = port,
            ["expression"] = "1+1"
        });
        Assert.True(evalOk.Success, evalOk.Detail);
        Assert.Contains("2", evalOk.Detail);

        var evalFail = await adapter.ExecuteAsync("eval", args: new Dictionary<string, object?>
        {
            ["port"] = port,
            ["expression"] = "foo"
        });
        Assert.False(evalFail.Success);
        Assert.Equal(ErrorCode.Internal, evalFail.Error);
        Assert.Contains("NameError", evalFail.Detail);

        await serverTask;
        Assert.Equal(4, receivedRequests.Count);
        Assert.Equal("ping", receivedRequests[0]["type"]?.GetValue<string>());
        Assert.Equal("execute", receivedRequests[1]["type"]?.GetValue<string>());
        Assert.Equal("result = 42", receivedRequests[1]["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task ExecuteAsync_UnreachablePort_ReturnsNotFoundWithInstallGuidance()
    {
        // Bind and immediately release an ephemeral port so nothing is listening on it
        int unusedPort;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            unusedPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        var adapter = new BlenderAdapter();
        var res = await adapter.ExecuteAsync("status", args: new Dictionary<string, object?>
        {
            ["port"] = unusedPort,
            ["timeoutMs"] = 5000
        });

        Assert.False(res.Success);
        Assert.Equal(ErrorCode.NotFound, res.Error);
        Assert.Contains("auto_install", res.Detail);
    }

    [Fact]
    public async Task ExecuteAsync_HungBridge_ReturnsTimeoutError()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var acceptTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(cts.Token);
            // Hold connection open without replying
            await Task.Delay(3000, cts.Token);
        }, cts.Token);

        var adapter = new BlenderAdapter();
        var res = await adapter.ExecuteAsync("ping", args: new Dictionary<string, object?>
        {
            ["port"] = port,
            ["timeoutMs"] = 500
        });

        Assert.False(res.Success);
        Assert.Equal(ErrorCode.Timeout, res.Error);
        Assert.Contains("did not answer", res.Detail);

        cts.Cancel();
        try { await acceptTask; } catch (OperationCanceledException) { }
    }
}
