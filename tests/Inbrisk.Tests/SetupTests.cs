using System.Text.Json;
using Inbrisk.Setup;
using Xunit;

namespace Inbrisk.Tests;

/// <summary>
/// Host-adapter + config-safety tests. Every adapter is exercised against a
/// synthetic HOME/APPDATA tree in temp — nothing touches the real user
/// profile. Invariants under test: other servers survive, malformed configs
/// are refused, connect is idempotent, disconnect removes only Inbrisk.
/// </summary>
public sealed class SetupTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"inbrisk-setuptest-{Guid.NewGuid():N}");
    private string Home => Path.Combine(_root, "home");
    private string Roaming => Path.Combine(_root, "roaming");
    private string Local => Path.Combine(_root, "local");
    private readonly string _exe = @"C:\Program Files\Inbrisk\inbrisk.exe";

    public SetupTests()
    {
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(Roaming);
        Directory.CreateDirectory(Local);
        Environment.SetEnvironmentVariable("INBRISK_DATA_DIR",
            Path.Combine(_root, "data"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("INBRISK_DATA_DIR", null);
        try { Directory.Delete(_root, true); } catch { }
    }

    private JsonFileHostAdapter Cursor() =>
        (JsonFileHostAdapter)HostRegistry.Find("cursor", Home, Roaming, Local)!;

    private JsonFileHostAdapter VsCode() =>
        (JsonFileHostAdapter)HostRegistry.Find("vscode", Home, Roaming, Local)!;

    [Fact]
    public void Connect_CreatesConfig_PreservingNothingElse()
    {
        var c = Cursor();
        var r = c.Connect(_exe);
        Assert.True(r.Ok && r.Changed);
        var doc = JsonDocument.Parse(File.ReadAllText(c.ConfigPath));
        var entry = doc.RootElement.GetProperty("mcpServers").GetProperty("inbrisk");
        Assert.Equal(_exe, entry.GetProperty("command").GetString());
        Assert.Equal("mcp", entry.GetProperty("args")[0].GetString());
    }

    [Fact]
    public void Connect_Preserves_Existing_Servers()
    {
        var c = Cursor();
        Directory.CreateDirectory(Path.GetDirectoryName(c.ConfigPath)!);
        File.WriteAllText(c.ConfigPath, """
            {
              "mcpServers": {
                "github": { "command": "npx", "args": ["-y", "gh-mcp"] },
                "filesystem": { "command": "node", "args": ["fs.js"] }
              }
            }
            """);
        var r = c.Connect(_exe);
        Assert.True(r.Ok);
        Assert.NotNull(r.BackupPath);
        Assert.True(File.Exists(r.BackupPath));

        var doc = JsonDocument.Parse(File.ReadAllText(c.ConfigPath));
        var servers = doc.RootElement.GetProperty("mcpServers");
        Assert.True(servers.TryGetProperty("github", out _));
        Assert.True(servers.TryGetProperty("filesystem", out _));
        Assert.True(servers.TryGetProperty("inbrisk", out _));
    }

    [Fact]
    public void Connect_Is_Idempotent()
    {
        var c = Cursor();
        Assert.True(c.Connect(_exe).Changed);
        var second = c.Connect(_exe);
        Assert.True(second.Ok);
        Assert.False(second.Changed);   // no duplicate, no rewrite
    }

    [Fact]
    public void Connect_Repoints_Stale_Path()
    {
        var c = Cursor();
        c.Connect(@"C:\Old\Location\inbrisk.exe");
        var r = c.Connect(_exe);
        Assert.True(r.Changed);
        var reg = c.GetInbriskEntry();
        Assert.NotNull(reg);
        Assert.True(reg!.PointsAt(_exe));
    }

    [Fact]
    public void Malformed_Config_Is_Refused_Not_Rewritten()
    {
        var c = Cursor();
        Directory.CreateDirectory(Path.GetDirectoryName(c.ConfigPath)!);
        File.WriteAllText(c.ConfigPath, "{ this is not json !!!");
        var r = c.Connect(_exe);
        Assert.False(r.Ok);
        Assert.Contains("malformed", r.Message, StringComparison.OrdinalIgnoreCase);
        // untouched
        Assert.Equal("{ this is not json !!!", File.ReadAllText(c.ConfigPath));
    }

    [Fact]
    public void Disconnect_Removes_Only_Inbrisk()
    {
        var c = Cursor();
        Directory.CreateDirectory(Path.GetDirectoryName(c.ConfigPath)!);
        File.WriteAllText(c.ConfigPath, """
            { "mcpServers": { "github": { "command": "npx" } } }
            """);
        c.Connect(_exe);
        var r = c.Disconnect();
        Assert.True(r.Ok && r.Changed);
        var doc = JsonDocument.Parse(File.ReadAllText(c.ConfigPath));
        var servers = doc.RootElement.GetProperty("mcpServers");
        Assert.True(servers.TryGetProperty("github", out _));
        Assert.False(servers.TryGetProperty("inbrisk", out _));
    }

    [Fact]
    public void VsCode_Uses_Servers_Key_Not_McpServers()
    {
        var v = VsCode();
        v.Connect(_exe);
        var doc = JsonDocument.Parse(File.ReadAllText(v.ConfigPath));
        Assert.True(doc.RootElement.TryGetProperty("servers", out var s));
        Assert.False(doc.RootElement.TryGetProperty("mcpServers", out _));
        var entry = s.GetProperty("inbrisk");
        Assert.Equal("stdio", entry.GetProperty("type").GetString());
        Assert.EndsWith(
            Path.Combine("Code", "User", "mcp.json").Replace('\\', Path.DirectorySeparatorChar),
            v.ConfigPath);
    }

    [Fact]
    public void Backup_Retention_Caps_At_Ten()
    {
        var backupDir = Path.Combine(_root, "backups");
        var cfg = new JsonConfigFile(Path.Combine(_root, "cfg.json"), backupDir);
        cfg.UpsertEntry("mcpServers", new System.Text.Json.Nodes.JsonObject
        { ["command"] = "a", ["args"] = new System.Text.Json.Nodes.JsonArray("mcp") });
        for (var i = 0; i < 15; i++)
        {
            cfg.UpsertEntry("mcpServers", new System.Text.Json.Nodes.JsonObject
            { ["command"] = $"a{i}", ["args"] = new System.Text.Json.Nodes.JsonArray("mcp") });
            Thread.Sleep(15); // distinct timestamps
        }
        var dir = Path.Combine(backupDir, "cfg.json");
        Assert.True(Directory.GetFiles(dir, "*.bak").Length <= 10);
    }

    [Fact]
    public void All_Adapters_Have_Distinct_Config_Paths()
    {
        var paths = HostRegistry.All(Home, Roaming, Local)
            .OfType<JsonFileHostAdapter>().Select(a => a.ConfigPath).ToList();
        Assert.Equal(paths.Count, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
