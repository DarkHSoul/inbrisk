using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Inbrisk.Setup;

/// <summary>What a host adapter learned about the machine.</summary>
public sealed record HostDetection(
    bool Installed, string? Version, string? ConfigPath,
    bool AutoConfigSupported, string Detail);

/// <summary>The inbrisk entry currently in a host's config, if present.</summary>
public sealed record HostRegistration(string Command, string Args, string ConfigPath)
{
    public bool PointsAt(string exePath) =>
        InstallLayout.PathsEqual(Command, exePath) && Args.Trim() == "mcp";
}

public sealed record HostMutationResult(
    bool Ok, bool Changed, string Message, string? ConfigPath = null,
    string? BackupPath = null);

public interface IMcpHostAdapter
{
    string Id { get; }
    string DisplayName { get; }
    /// <summary>Does the host need a restart/new session for config changes?</summary>
    bool RequiresRestart { get; }
    HostDetection Detect();
    HostRegistration? GetInbriskEntry();
    HostMutationResult Connect(string exePath);
    HostMutationResult Disconnect();
    string ManualSetup(string exePath);
}

/// <summary>Locate an executable on PATH (name, name.exe, name.cmd).</summary>
public static class PathSearch
{
    public static string? OnPath(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in new[] { "", ".exe", ".cmd", ".bat" })
            {
                var p = Path.Combine(dir.Trim(), name + ext);
                if (File.Exists(p)) return p;
            }
        }
        return null;
    }

    public static (int Code, string Stdout, string Stderr) Run(
        string file, string args, int timeoutMs = 30000)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(); } catch { }
            return (-1, "", "timeout");
        }
        return (p.ExitCode, so.Result, se.Result);
    }

    public static string? FileVersionOf(string path)
    {
        try { return FileVersionInfo.GetVersionInfo(path).ProductVersion; }
        catch { return null; }
    }
}

/// <summary>
/// Adapter for hosts whose MCP config is a JSON file with a root key
/// ("mcpServers" or "servers") holding {command, args} entries.
/// </summary>
public class JsonFileHostAdapter : IMcpHostAdapter
{
    private readonly Func<string> _configPath;
    private readonly Func<bool> _installed;
    private readonly Func<string?> _version;
    private readonly bool _stdioTypeField;

    public string Id { get; }
    public string DisplayName { get; }
    public virtual bool RequiresRestart => true;

    public JsonFileHostAdapter(string id, string displayName,
        string rootKey, Func<string> configPath, Func<bool> installed,
        Func<string?>? version = null, bool stdioTypeField = false)
    {
        Id = id; DisplayName = displayName; RootKey = rootKey;
        _configPath = configPath; _installed = installed;
        _version = version ?? (() => null);
        _stdioTypeField = stdioTypeField;
    }

    public string RootKey { get; }
    public string ConfigPath => _configPath();
    protected JsonConfigFile Config => new(ConfigPath);

    public virtual HostDetection Detect() => new(
        Installed: _installed(),
        Version: _version(),
        ConfigPath: File.Exists(ConfigPath) || _installed() ? ConfigPath : null,
        AutoConfigSupported: true,
        Detail: _installed()
            ? $"config: {ConfigPath} (key '{RootKey}')"
            : "not detected");

    public virtual HostRegistration? GetInbriskEntry()
    {
        try
        {
            var entry = Config.GetEntry(RootKey);
            if (entry == null) return null;
            var (cmd, args) = JsonConfigFile.Describe(entry);
            return cmd == null ? null : new HostRegistration(cmd, args, ConfigPath);
        }
        catch (ConfigMalformedException) { throw; }
        catch { return null; }
    }

    protected virtual JsonObject BuildEntry(string exePath)
    {
        var e = new JsonObject
        {
            ["command"] = exePath,
            ["args"] = new JsonArray("mcp"),
        };
        if (_stdioTypeField) e["type"] = "stdio";
        return e;
    }

    public virtual HostMutationResult Connect(string exePath)
    {
        try
        {
            var existing = GetInbriskEntry();
            if (existing != null && existing.PointsAt(exePath))
                return new(true, false, "already configured", ConfigPath);
            var r = Config.UpsertEntry(RootKey, BuildEntry(exePath));
            return new(true, r.Changed,
                r.Changed
                    ? (existing == null ? "registered" : "updated (old entry replaced)")
                    : "already configured",
                ConfigPath, r.BackupPath);
        }
        catch (ConfigMalformedException e) { return new(false, false, e.Message, ConfigPath); }
        catch (Exception e) { return new(false, false, $"write failed: {e.Message}", ConfigPath); }
    }

    public virtual HostMutationResult Disconnect()
    {
        try
        {
            var r = Config.RemoveEntry(RootKey);
            return new(true, r.Changed,
                r.Changed ? "registration removed" : "not configured", ConfigPath, r.BackupPath);
        }
        catch (ConfigMalformedException e) { return new(false, false, e.Message, ConfigPath); }
        catch (Exception e) { return new(false, false, $"write failed: {e.Message}", ConfigPath); }
    }

    /// <summary>Entry exists, points at the given exe, exe exists, args right.</summary>
    public bool Verify(string exePath)
    {
        var reg = GetInbriskEntry();
        return reg != null && reg.PointsAt(exePath) && File.Exists(reg.Command);
    }

    public virtual string ManualSetup(string exePath)
    {
        var entry = BuildEntry(exePath).ToJsonString();
        return $"""
            {DisplayName} is not auto-configurable from here, or its config is
            not writable. Add the following under the "{RootKey}" object in:

              {ConfigPath}

            Entry:

              "inbrisk": {entry}

            Then restart {DisplayName} and ask it to list its MCP tools —
            computer_observe should be present.
            """;
    }
}

/// <summary>
/// Adapter for hosts that ship a first-party `x mcp add` CLI (Devin, Claude
/// Code). The CLI is preferred — it owns schema/migration concerns — and the
/// documented config file is the fallback when the CLI is absent.
/// </summary>
public sealed class CliFirstHostAdapter : JsonFileHostAdapter
{
    private readonly string _cliName;
    private readonly string _addArgs;   // e.g. "mcp add -s user inbrisk -- {exe} mcp"
    private readonly string _removeArgs;

    public CliFirstHostAdapter(string id, string displayName, string cliName,
        string rootKey, Func<string> configPath, Func<bool> installed,
        Func<string?>? version, string addArgs, string removeArgs)
        : base(id, displayName, rootKey, configPath, installed, version)
    {
        _cliName = cliName; _addArgs = addArgs; _removeArgs = removeArgs;
    }

    public string? CliPath => PathSearch.OnPath(_cliName);

    public override HostDetection Detect()
    {
        var d = base.Detect();
        var cli = CliPath;
        return d with
        {
            AutoConfigSupported = true,
            Detail = d.Detail + (cli != null ? $" | CLI: {cli}" : " | CLI not on PATH — file fallback"),
        };
    }

    public override HostMutationResult Connect(string exePath)
    {
        var existing = GetInbriskEntry();
        if (existing != null && existing.PointsAt(exePath))
            return new(true, false, "already configured", ConfigPath);

        var cli = CliPath;
        if (cli != null)
        {
            var (code, stdout, stderr) = PathSearch.Run(cli,
                _addArgs.Replace("{exe}", $"\"{exePath}\""));
            if (code == 0)
                return new(true, true, $"registered via {_cliName} CLI: {stdout.Trim()}",
                    ConfigPath);
            // CLI failed — fall through to file write rather than silently win
        }
        return base.Connect(exePath);
    }

    public override HostMutationResult Disconnect()
    {
        var cli = CliPath;
        if (cli != null && GetInbriskEntry() != null)
        {
            var (code, _, stderr) = PathSearch.Run(cli, _removeArgs);
            if (code == 0)
                return new(true, true, $"removed via {_cliName} CLI", ConfigPath);
        }
        return base.Disconnect();
    }
}

public static class HostRegistry
{
    private static string Home =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string Roaming =>
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static string Local =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>All supported adapters. Each was built from verified vendor
    /// docs — a host whose config mechanism we could not verify is absent
    /// rather than guessed.</summary>
    public static IReadOnlyList<IMcpHostAdapter> All(string? homeOverride = null,
        string? roamingOverride = null, string? localOverride = null)
    {
        var home = homeOverride ?? Home;
        var roaming = roamingOverride ?? Roaming;
        var local = localOverride ?? Local;

        string? ClaudeDesktopConfig()
        {
            // Windows MSIX packaging virtualizes %APPDATA% — the real config
            // lives under the package's LocalCache. Prefer it when present.
            var pkgDir = Path.Combine(local, "Packages");
            if (Directory.Exists(pkgDir))
            {
                var msix = Directory.GetDirectories(pkgDir, "Claude_*")
                    .Select(d => Path.Combine(d, "LocalCache", "Roaming", "Claude",
                        "claude_desktop_config.json"))
                    .FirstOrDefault(p => File.Exists(p) ||
                        Directory.Exists(Path.GetDirectoryName(p)!));
                if (msix != null) return msix;
            }
            return Path.Combine(roaming, "Claude", "claude_desktop_config.json");
        }

        return new IMcpHostAdapter[]
        {
            new CliFirstHostAdapter("devin", "Devin", "devin",
                rootKey: "mcpServers",
                configPath: () => Path.Combine(roaming, "devin", "mcp_config.json"),
                installed: () => PathSearch.OnPath("devin") != null ||
                    File.Exists(Path.Combine(local, "Programs", "Devin", "Devin.exe")) ||
                    Directory.Exists(Path.Combine(local, "Programs", "Devin")),
                version: () => PathSearch.FileVersionOf(
                    Path.Combine(local, "Programs", "Devin", "Devin.exe")),
                addArgs: "mcp add -s user inbrisk -- {exe} mcp",
                removeArgs: "mcp remove -s user inbrisk"),

            new CliFirstHostAdapter("claude-code", "Claude Code", "claude",
                rootKey: "mcpServers",
                configPath: () => Path.Combine(home, ".claude.json"),
                installed: () => PathSearch.OnPath("claude") != null ||
                    File.Exists(Path.Combine(home, ".claude.json")) ||
                    Directory.Exists(Path.Combine(home, ".claude")),
                version: null,
                addArgs: "mcp add --scope user inbrisk -- {exe} mcp",
                removeArgs: "mcp remove --scope user inbrisk"),

            new JsonFileHostAdapter("cursor", "Cursor",
                rootKey: "mcpServers",
                configPath: () => Path.Combine(home, ".cursor", "mcp.json"),
                installed: () => PathSearch.OnPath("cursor") != null ||
                    Directory.Exists(Path.Combine(local, "Programs", "cursor")) ||
                    Directory.Exists(Path.Combine(home, ".cursor")),
                version: () => PathSearch.FileVersionOf(
                    Path.Combine(local, "Programs", "cursor", "Cursor.exe"))),

            new JsonFileHostAdapter("vscode", "VS Code",
                rootKey: "servers",   // VS Code uses "servers", NOT "mcpServers"
                configPath: () => Path.Combine(roaming, "Code", "User", "mcp.json"),
                installed: () => PathSearch.OnPath("code") != null ||
                    File.Exists(Path.Combine(local, "Programs", "Microsoft VS Code", "Code.exe")),
                version: () => PathSearch.FileVersionOf(
                    Path.Combine(local, "Programs", "Microsoft VS Code", "Code.exe")),
                stdioTypeField: true),

            new JsonFileHostAdapter("claude-desktop", "Claude Desktop",
                rootKey: "mcpServers",
                configPath: () => ClaudeDesktopConfig()!,
                installed: () => Directory.Exists(Path.Combine(roaming, "Claude")) ||
                    (Directory.Exists(Path.Combine(local, "Packages")) &&
                     Directory.GetDirectories(Path.Combine(local, "Packages"), "Claude_*").Length > 0)),

            new JsonFileHostAdapter("windsurf", "Windsurf",
                rootKey: "mcpServers",
                configPath: () => Path.Combine(home, ".codeium", "windsurf", "mcp_config.json"),
                installed: () => PathSearch.OnPath("windsurf") != null ||
                    Directory.Exists(Path.Combine(home, ".codeium", "windsurf")) ||
                    Directory.Exists(Path.Combine(local, "Programs", "Windsurf"))),
        };
    }

    public static IMcpHostAdapter? Find(string idOrName,
        string? home = null, string? roaming = null, string? local = null) =>
        All(home, roaming, local).FirstOrDefault(a =>
            a.Id.Equals(idOrName, StringComparison.OrdinalIgnoreCase) ||
            a.DisplayName.Equals(idOrName, StringComparison.OrdinalIgnoreCase));
}
