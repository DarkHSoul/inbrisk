using Inbrisk.Platform.Windows.Apps;

namespace Inbrisk.Tests;

[Trait("Category", "Unit")]
public sealed class LaunchPathGateTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Classify_EmptyOrWhitespace_ReturnsDenied(string? rawPath)
    {
        var d = LaunchPathGate.Classify(rawPath);
        Assert.Equal(LaunchPathGate.Verdict.Denied, d.Verdict);
        Assert.NotNull(d.Detail);
    }

    [Theory]
    [InlineData("C:\\Apps\\tool.exe\ncmd.exe")]
    [InlineData("C:\\Apps\\tool\r.exe")]
    [InlineData("C:\\Apps\\tool\0.exe")]
    [InlineData("C:\\Apps\\tool\t.exe")]
    public void Classify_ControlCharacters_ReturnsDenied(string rawPath)
    {
        var d = LaunchPathGate.Classify(rawPath);
        Assert.Equal(LaunchPathGate.Verdict.Denied, d.Verdict);
        Assert.Contains("control character", d.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("C:\\Apps\\bad*name.exe")]
    [InlineData("C:\\Apps\\bad?name.exe")]
    [InlineData("C:\\Apps\\bad<name>.exe")]
    [InlineData("C:\\Apps\\pipe|cmd.exe")]
    [InlineData("C:\\Apps\\ads.txt:hidden.exe")]
    [InlineData("C:\\Apps\\")]
    public void Classify_IllegalLeafCharsOrNtfsAds_ReturnsDenied(string rawPath)
    {
        var d = LaunchPathGate.Classify(rawPath);
        Assert.Equal(LaunchPathGate.Verdict.Denied, d.Verdict);
        Assert.Contains("illegal filename", d.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(@"\\attacker-server\share\payload.exe")]
    [InlineData(@"\\10.0.0.5\c$\evil.bat")]
    [InlineData("//attacker-server/share/payload.exe")]
    public void Classify_UncPaths_ReturnsDenied(string rawPath)
    {
        var d = LaunchPathGate.Classify(rawPath);
        Assert.Equal(LaunchPathGate.Verdict.Denied, d.Verdict);
        Assert.Contains("UNC", d.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(@"C:\Program Files\MyApp\MyApp.exe", ".exe")]
    [InlineData(@"  ""C:\Program Files\MyApp\MyApp.exe""  ", ".exe")]
    [InlineData(@"\\?\C:\VeryLongPath\MyApp.exe", ".exe")]
    [InlineData(@"C:\Projects\Game\Game.uproject", ".uproject")]
    [InlineData(@"C:\Docs\report.pdf", ".pdf")]
    public void Classify_ExecutablesAndDocuments_ReturnsAllowed(string rawPath, string expectedExt)
    {
        var d = LaunchPathGate.Classify(rawPath);
        Assert.Equal(LaunchPathGate.Verdict.Allowed, d.Verdict);
        Assert.Equal(expectedExt, d.Extension, ignoreCase: true);
        Assert.Null(d.Detail);
    }

    [Theory]
    [InlineData(@"C:\Scripts\run.bat", ".bat")]
    [InlineData(@"C:\Scripts\run.cmd", ".cmd")]
    [InlineData(@"C:\Scripts\deploy.ps1", ".ps1")]
    [InlineData(@"C:\Scripts\DEPLOY.PS1", ".PS1")]
    [InlineData(@"C:\Scripts\macro.vbs", ".vbs")]
    [InlineData(@"C:\Scripts\macro.vbe", ".vbe")]
    [InlineData(@"C:\Scripts\payload.js", ".js")]
    [InlineData(@"C:\Scripts\payload.jse", ".jse")]
    [InlineData(@"C:\Scripts\job.wsf", ".wsf")]
    [InlineData(@"C:\Scripts\app.hta", ".hta")]
    [InlineData(@"C:\Install\setup.msi", ".msi")]
    [InlineData(@"C:\Install\tweak.reg", ".reg")]
    [InlineData(@"C:\Install\link.url", ".url")]
    [InlineData(@"C:\Install\screen.scr", ".scr")]
    [InlineData(@"C:\Install\legacy.com", ".com")]
    [InlineData(@"C:\Install\legacy.pif", ".pif")]
    [InlineData(@"C:\Install\console.msc", ".msc")]
    [InlineData(@"C:\Install\panel.cpl", ".cpl")]
    public void Classify_ScriptAndInstallerExtensions_RequireConsent(string rawPath, string expectedExt)
    {
        var d = LaunchPathGate.Classify(rawPath);
        Assert.Equal(LaunchPathGate.Verdict.ScriptRequiresConsent, d.Verdict);
        Assert.Equal(expectedExt, d.Extension, ignoreCase: true);
        Assert.Contains("arbitrary code", d.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Classify_UnresolvableLnk_FailsClosedToScriptRequiresConsent()
    {
        var bogusLnk = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid():N}.lnk");
        var d = LaunchPathGate.Classify(bogusLnk);
        Assert.Equal(LaunchPathGate.Verdict.ScriptRequiresConsent, d.Verdict);
        Assert.Equal(".lnk", d.Extension, ignoreCase: true);
        Assert.Contains("could not be resolved", d.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_ScriptRequiresConsent_BlockedByDefaultAndAllowedWithEnvConsent()
    {
        var prev = Environment.GetEnvironmentVariable(LaunchPathGate.AllowEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(LaunchPathGate.AllowEnvVar, null);
            var blocked = LaunchPathGate.Evaluate(@"C:\Scripts\build.ps1");
            Assert.Equal(LaunchPathGate.Verdict.ScriptRequiresConsent, blocked.Verdict);

            Environment.SetEnvironmentVariable(LaunchPathGate.AllowEnvVar, "1");
            var allowed = LaunchPathGate.Evaluate(@"C:\Scripts\build.ps1");
            Assert.Equal(LaunchPathGate.Verdict.Allowed, allowed.Verdict);
            Assert.Contains(LaunchPathGate.AllowEnvVar, allowed.Detail);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LaunchPathGate.AllowEnvVar, prev);
        }
    }

    [Fact]
    public void Evaluate_DeniedTarget_NeverOverriddenByEnvConsent()
    {
        var prev = Environment.GetEnvironmentVariable(LaunchPathGate.AllowEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(LaunchPathGate.AllowEnvVar, "true");

            var unc = LaunchPathGate.Evaluate(@"\\evil-host\share\payload.ps1");
            Assert.Equal(LaunchPathGate.Verdict.Denied, unc.Verdict);

            var ctrl = LaunchPathGate.Evaluate("C:\\Apps\\ok.exe\nwhoami");
            Assert.Equal(LaunchPathGate.Verdict.Denied, ctrl.Verdict);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LaunchPathGate.AllowEnvVar, prev);
        }
    }
}
