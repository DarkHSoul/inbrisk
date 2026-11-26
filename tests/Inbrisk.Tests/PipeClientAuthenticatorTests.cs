using System.IO.Pipes;
using System.Reflection;
using Inbrisk.Mcp.Daemon;

namespace Inbrisk.Tests;

[Trait("Category", "Contract")]
public sealed class PipeClientAuthenticatorTests
{
    private static bool InvokeIsAllowedPeer(string exeName, string? exePath)
    {
        var m = typeof(PipeClientAuthenticator).GetMethod(
            "IsAllowedPeer", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(m);
        return (bool)m!.Invoke(null, new object?[] { exeName, exePath })!;
    }

    private static bool InvokeIsUnderDirectory(string path, string dir)
    {
        var m = typeof(PipeClientAuthenticator).GetMethod(
            "IsUnderDirectory", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(m);
        return (bool)m!.Invoke(null, new object?[] { path, dir })!;
    }

    [Fact]
    public async Task TryVerifyClient_InProcessSamePid_SucceedsAsSelf()
    {
        var prevAuth = Environment.GetEnvironmentVariable("INBRISK_IPC_AUTH");
        try
        {
            Environment.SetEnvironmentVariable("INBRISK_IPC_AUTH", null);
            var pipeName = $"inbrisk-auth-test-{Guid.NewGuid():N}";
            using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous);

            var waitTask = server.WaitForConnectionAsync();
            await client.ConnectAsync(3000);
            await waitTask;

            var ok = PipeClientAuthenticator.TryVerifyClient(
                server, out var peerDesc, out var rejectReason, out var pid);

            Assert.True(ok, $"Expected self-connection to pass, got reject: {rejectReason}");
            Assert.Null(rejectReason);
            Assert.Equal(Environment.ProcessId, pid);
            Assert.Contains("(self)", peerDesc);
        }
        finally
        {
            Environment.SetEnvironmentVariable("INBRISK_IPC_AUTH", prevAuth);
        }
    }

    [Fact]
    public void TryVerifyClient_UnconnectedPipe_FailsClosed()
    {
        var prevAuth = Environment.GetEnvironmentVariable("INBRISK_IPC_AUTH");
        try
        {
            Environment.SetEnvironmentVariable("INBRISK_IPC_AUTH", null);
            var pipeName = $"inbrisk-auth-unconn-{Guid.NewGuid():N}";
            using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

            var ok = PipeClientAuthenticator.TryVerifyClient(
                server, out _, out var rejectReason, out var pid);

            Assert.False(ok);
            Assert.NotNull(rejectReason);
            Assert.Equal(0, pid);
        }
        finally
        {
            Environment.SetEnvironmentVariable("INBRISK_IPC_AUTH", prevAuth);
        }
    }

    [Fact]
    public void TryVerifyClient_AuthDisabledEnv_BypassesVerification()
    {
        var prevAuth = Environment.GetEnvironmentVariable("INBRISK_IPC_AUTH");
        try
        {
            Environment.SetEnvironmentVariable("INBRISK_IPC_AUTH", "0");
            var pipeName = $"inbrisk-auth-off-{Guid.NewGuid():N}";
            using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

            var ok = PipeClientAuthenticator.TryVerifyClient(
                server, out _, out var rejectReason, out _);

            Assert.True(ok);
            Assert.Null(rejectReason);
        }
        finally
        {
            Environment.SetEnvironmentVariable("INBRISK_IPC_AUTH", prevAuth);
        }
    }

    [Theory]
    [InlineData("inbrisk", @"C:\Somewhere\inbrisk.exe", true)]
    [InlineData("inbrisk-mcp", @"C:\Somewhere\inbrisk-mcp.exe", true)]
    [InlineData("INBRISK-MCP", null, true)]
    [InlineData("powershell", @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", false)]
    [InlineData("cmd", @"C:\Windows\System32\cmd.exe", false)]
    [InlineData("evil-client", @"C:\Temp\evil-client.exe", false)]
    public void IsAllowedPeer_AllowlistsOnlyInbriskBinariesByDefault(
        string exeName, string? exePath, bool expected)
    {
        var prevExtra = Environment.GetEnvironmentVariable("INBRISK_IPC_ALLOW_EXE");
        try
        {
            Environment.SetEnvironmentVariable("INBRISK_IPC_ALLOW_EXE", null);
            Assert.Equal(expected, InvokeIsAllowedPeer(exeName, exePath));
        }
        finally
        {
            Environment.SetEnvironmentVariable("INBRISK_IPC_ALLOW_EXE", prevExtra);
        }
    }

    [Fact]
    public void IsAllowedPeer_OperatorEnvAllowlist_MatchesNameOrFullPathOnly()
    {
        var prevExtra = Environment.GetEnvironmentVariable("INBRISK_IPC_ALLOW_EXE");
        try
        {
            Environment.SetEnvironmentVariable("INBRISK_IPC_ALLOW_EXE",
                @"custom-host; C:\Approved\runner.exe");

            Assert.True(InvokeIsAllowedPeer("custom-host", @"D:\Any\custom-host.exe"));
            Assert.True(InvokeIsAllowedPeer("runner", @"C:\Approved\runner.exe"));
            Assert.False(InvokeIsAllowedPeer("runner", @"C:\Unapproved\runner.exe"));
            Assert.False(InvokeIsAllowedPeer("other-app", @"C:\Temp\other-app.exe"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("INBRISK_IPC_ALLOW_EXE", prevExtra);
        }
    }

    [Fact]
    public void IsUnderDirectory_PreventsSiblingPrefixSpoofing()
    {
        Assert.True(InvokeIsUnderDirectory(@"C:\Program Files\Inbrisk\tool.exe", @"C:\Program Files\Inbrisk"));
        Assert.True(InvokeIsUnderDirectory(@"C:\Program Files\Inbrisk\sub\tool.exe", @"C:\Program Files\Inbrisk\"));
        // Sibling directory sharing the same prefix MUST be rejected
        Assert.False(InvokeIsUnderDirectory(@"C:\Program Files\InbriskEvil\tool.exe", @"C:\Program Files\Inbrisk"));
        Assert.False(InvokeIsUnderDirectory(@"C:\Temp\tool.exe", @"C:\Program Files\Inbrisk"));
    }
}
