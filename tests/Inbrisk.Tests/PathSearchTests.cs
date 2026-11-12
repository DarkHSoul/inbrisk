using Inbrisk.Setup;
using Xunit;

namespace Inbrisk.Tests;

/// <summary>
/// Regression: npm installs an extensionless shell shim next to name.cmd
/// (e.g. %APPDATA%\npm\claude). PathSearch used to return the shim, which
/// Windows cannot execute (Win32Exception 193) and crashed `inbrisk setup`.
/// </summary>
[Collection("PathEnv")]
public sealed class PathSearchTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), $"inbrisk-pathtest-{Guid.NewGuid():N}");
    private readonly string? _oldPath = Environment.GetEnvironmentVariable("PATH");

    public PathSearchTests()
    {
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable("PATH", _dir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PATH", _oldPath);
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void OnPath_PrefersCmdOverExtensionlessNpmShim()
    {
        File.WriteAllText(Path.Combine(_dir, "fakecli"), "#!/bin/sh\n");
        var cmd = Path.Combine(_dir, "fakecli.cmd");
        File.WriteAllText(cmd, "@echo off\r\n");

        Assert.Equal(cmd, PathSearch.OnPath("fakecli"));
    }

    [Fact]
    public void OnPath_IgnoresExtensionlessFileWhenNothingRunnableExists()
    {
        File.WriteAllText(Path.Combine(_dir, "onlyshim"), "#!/bin/sh\n");

        Assert.Null(PathSearch.OnPath("onlyshim"));
    }

    [Fact]
    public void Run_ReturnsErrorInsteadOfThrowing_WhenProcessCannotStart()
    {
        var shim = Path.Combine(_dir, "notanexe");
        File.WriteAllText(shim, "#!/bin/sh\n");

        var (code, _, stderr) = PathSearch.Run(shim, "");

        Assert.Equal(-1, code);
        Assert.Contains("could not start", stderr);
    }
}
