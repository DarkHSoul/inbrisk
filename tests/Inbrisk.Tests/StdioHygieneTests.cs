using Inbrisk.Setup;
using Xunit;

namespace Inbrisk.Tests;

/// <summary>
/// §6 regression: `inbrisk mcp` stdout must carry ONLY MCP protocol frames.
/// McpSelfTest does a real handshake and flags any non-JSON-RPC stdout line —
/// a stray banner/Console.WriteLine fails here immediately.
/// </summary>
[Collection("desktop")]
public sealed class StdioHygieneTests
{
    private readonly DesktopFixture _fx;
    public StdioHygieneTests(DesktopFixture fx) => _fx = fx;

    [Fact]
    public async Task McpStdout_IsProtocolClean()
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "inbrisk-mcp.dll");
        Assert.True(File.Exists(dll));
        var r = await McpSelfTest.RunAsync("dotnet", $"\"{dll}\"");
        Assert.True(r.Ok, r.Error);
        Assert.True(r.StdoutClean);
        Assert.Equal("inbrisk", r.ServerName);
        Assert.True(r.HasComputerRun);
        Assert.True(r.InstructionsPresent);
        Assert.True(r.CleanDisconnect);
    }
}
