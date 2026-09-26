using System.Text.Json;

namespace Inbrisk.Setup;

/// <summary>
/// Production command surface: setup / install / doctor / hosts / connect /
/// disconnect / status / version / repair / uninstall / update. These run
/// BEFORE the computer-control runtime exists — none of them may show the
/// screen indicator or touch input.
/// </summary>
public static class SetupCommands
{
    public static bool IsSetupCommand(string cmd) => cmd.ToLowerInvariant() is
        "setup" or "install" or "doctor" or "hosts" or "connect"
        or "disconnect" or "status" or "version" or "repair"
        or "uninstall" or "update";

    private static bool Has(string[] a, string f) =>
        a.Contains(f, StringComparer.OrdinalIgnoreCase);
    private static string? Opt(string[] a, string f) =>
        a.SkipWhile(x => x != f).Skip(1).FirstOrDefault(x => !x.StartsWith("--"));

    public static async Task<int> RunAsync(string[] args,
        Func<Task<(bool uia, bool capture, bool input, bool dpi)>>? probes = null)
    {
        var cmd = args[0].ToLowerInvariant();
        var exe = InstallLayout.CanonicalExePath;
        switch (cmd)
        {
            case "version": return CmdVersion(args);
            case "status": return CmdStatus(args);
            case "hosts": return CmdHosts();
            case "connect": return CmdConnect(args, exe);
            case "disconnect": return CmdDisconnect(args);
            case "doctor": return await CmdDoctor(args, exe, probes);
            case "install": return CmdInstall(args);
            case "uninstall": return CmdUninstall(args);
            case "update": return await CmdUpdate(args);
            case "repair": return CmdRepair(exe);
            case "setup": return await CmdSetup(args, exe, probes);
            default: return 2;
        }
    }

    // ------------------------------------------------------------ commands

    private static int CmdVersion(string[] args)
    {
        var exe = InstallLayout.ProcessExePath;
        if (Has(args, "--json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                name = "inbrisk",
                version = InstallLayout.Version,
                buildCommit = InstallLayout.BuildCommit,
                buildTimestamp = InstallLayout.BuildTimestamp,
                codeSha256 = InstallLayout.SelfSha256(),
                installed = InstallLayout.IsInstalled,
                path = exe,
                runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                os = Environment.OSVersion.ToString(),
                arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
            Console.WriteLine($"inbrisk {InstallLayout.Version} " +
                $"({InstallLayout.BuildCommit ?? "dev"}, sha256 {InstallLayout.SelfSha256()[..12]}…)");
        return 0;
    }

    private static int CmdStatus(string[] args)
    {
        var hosts = HostRegistry.All().Select(h =>
        {
            var d = h.Detect();
            HostRegistration? reg = null;
            string? cfgError = null;
            try { reg = h.GetInbriskEntry(); }
            catch (ConfigMalformedException) { cfgError = "malformed config"; }
            return new
            {
                id = h.Id, name = h.DisplayName, installed = d.Installed,
                configured = reg != null,
                registrationPath = reg?.Command,
                configPath = d.ConfigPath,
                requiresRestart = h.RequiresRestart,
                error = cfgError,
            };
        }).ToArray();

        var emergency = EmergencyProbe.Read();
        var emergencyStopped = emergency.StopMarkerPresent;
        var pendingUpdate = File.Exists(InstallLayout.PendingUpdateMarker);

        if (Has(args, "--json"))
        {
            // machine-readable for AI/automation — no secrets, no logs, no PII
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                version = InstallLayout.Version,
                buildCommit = InstallLayout.BuildCommit,
                codeSha256 = InstallLayout.SelfSha256(),
                installed = InstallLayout.IsInstalled,
                installDir = InstallLayout.IsInstalled ? InstallLayout.ProcessDir : null,
                mcp = new { transport = "stdio", activeSessions = ActiveMcpSessions() },
                emergencyStopped,
                controlState = emergency.ControlState,
                panicAuthority = emergency.AuthorityPid is { } ap
                    ? new { pid = ap, alive = emergency.AuthorityAlive } : null,
                pendingUpdate,
                hosts,
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            Console.WriteLine($"inbrisk {InstallLayout.Version}" +
                (InstallLayout.IsInstalled ? $" @ {InstallLayout.ProcessDir}" : " (not installed)"));
            Console.WriteLine($"emergency stopped: {emergencyStopped}  pending update: {pendingUpdate}");
            Console.WriteLine($"control state: {emergency.ControlState} — {emergency.Detail}");
            foreach (var h in hosts)
                Console.WriteLine($"  {(h.installed ? "✓" : "○")} {h.name,-15}" +
                    (h.error != null ? $" ERROR: {h.error}"
                        : h.configured ? " configured"
                        : h.installed ? " not configured" : " not installed"));
        }
        return 0;
    }

    private static int ActiveMcpSessions()
    {
        try
        {
            return System.Diagnostics.Process.GetProcessesByName("inbrisk")
                .Count(p => p.Id != Environment.ProcessId);
        }
        catch { return 0; }
    }

    private static int CmdHosts()
    {
        foreach (var h in HostRegistry.All())
        {
            var d = h.Detect();
            Console.WriteLine($"{h.Id,-15} {h.DisplayName,-15} " +
                $"{(d.Installed ? "detected" : "not found"),-12} " +
                $"{(d.AutoConfigSupported ? "auto-config" : "manual"),-12} " +
                $"{(h.RequiresRestart ? "restart/new-session" : "live")}");
            if (d.Installed)
                Console.WriteLine($"    {d.Detail}");
        }
        return 0;
    }

    private static int CmdConnect(string[] args, string exe)
    {
        var id = args.ElementAtOrDefault(1)
            ?? throw new UsageException("connect requires a host id (see `inbrisk hosts`)");
        var host = HostRegistry.Find(id)
            ?? throw new UsageException($"unknown host '{id}' — see `inbrisk hosts`");
        var d = host.Detect();
        if (!d.Installed)
            Console.WriteLine($"warning: {host.DisplayName} not detected — configuring anyway");

        var r = host.Connect(exe);
        Console.WriteLine($"{host.DisplayName}: {r.Message}");
        if (r.ConfigPath != null) Console.WriteLine($"  config: {r.ConfigPath}");
        if (r.BackupPath != null) Console.WriteLine($"  backup: {r.BackupPath}");
        if (!r.Ok) return 1;
        if (host.RequiresRestart)
            Console.WriteLine($"Configured — restart {host.DisplayName} (or start a new session) to activate.");
        return 0;
    }

    private static int CmdDisconnect(string[] args)
    {
        var id = args.ElementAtOrDefault(1)
            ?? throw new UsageException("disconnect requires a host id");
        var host = HostRegistry.Find(id)
            ?? throw new UsageException($"unknown host '{id}'");
        var r = host.Disconnect();
        Console.WriteLine($"{host.DisplayName}: {r.Message}");
        return r.Ok ? 0 : 1;
    }

    private static async Task<int> CmdDoctor(string[] args, string exe,
        Func<Task<(bool, bool, bool, bool)>>? probes)
    {
        var realExe = File.Exists(exe) ? exe : InstallLayout.ProcessExePath;
        var sections = await Doctor.RunAsync(realExe, probes);
        if (Has(args, "--json")) Console.WriteLine(Doctor.ToJson(sections));
        else Doctor.Print(sections);
        return sections.SelectMany(s => s.Checks).All(c => c.Ok) ? 0 : 1;
    }

    private static int CmdInstall(string[] args)
    {
        var machine = Has(args, "--machine");
        var r = SelfInstaller.Install(machine);
        Console.WriteLine(r.Message);
        return r.Ok ? 0 : 1;
    }

    private static int CmdUninstall(string[] args)
    {
        var autoYes = Has(args, "--yes") || Has(args, "-y") || Has(args, "--quiet");
        var keepData = Has(args, "--keep-data");
        var r = SelfInstaller.Uninstall(keepData, autoYes,
            confirm: q => Prompt(q));
        foreach (var a in r.Actions) Console.WriteLine($"  ✓ {a}");
        foreach (var e in r.Errors) Console.WriteLine($"  ✗ {e}");
        Console.WriteLine(r.Ok ? "Inbrisk uninstalled." : "Uninstall finished with errors.");
        return r.Ok ? 0 : 1;
    }

    private static async Task<int> CmdUpdate(string[] args)
    {
        var manifest = Opt(args, "--manifest");
        var r = await SelfInstaller.UpdateAsync(manifest);
        Console.WriteLine(r.Message);
        return r.Ok ? 0 : 1;
    }

    /// <summary>Repair mechanics shared by the CLI and the setup app:
    /// rewrite missing install metadata, repoint stale host registrations.</summary>
    public static (List<string> Mutations, List<string> Problems) RepairInstall(string exe)
    {
        var mutations = new List<string>();
        var problems = new List<string>();

        // install metadata missing while installed → rewrite
        if (InstallLayout.IsInstalled)
        {
            var meta = Path.Combine(InstallLayout.ProcessDir, "install-metadata.json");
            if (!File.Exists(meta))
            {
                File.WriteAllText(meta, JsonSerializer.Serialize(new
                {
                    version = InstallLayout.Version,
                    buildCommit = InstallLayout.BuildCommit,
                    repairedAt = DateTimeOffset.UtcNow,
                }, new JsonSerializerOptions { WriteIndented = true }));
                mutations.Add("rewrote install-metadata.json");
            }
            if (!File.Exists(Path.Combine(InstallLayout.ProcessDir, InstallLayout.ExeName)))
                problems.Add("inbrisk.exe missing from install dir — reinstall");
        }

        // host registrations pointing at a stale exe path → repoint
        foreach (var h in HostRegistry.All())
        {
            HostRegistration? reg;
            try { reg = h.GetInbriskEntry(); }
            catch (ConfigMalformedException e)
            { problems.Add($"{h.DisplayName}: {e.Message}"); continue; }
            if (reg == null) continue;
            if (!reg.PointsAt(exe) || !File.Exists(reg.Command))
            {
                var r = h.Connect(exe);   // upsert is scoped to our entry only
                (r.Ok ? mutations : problems).Add(
                    $"{h.DisplayName}: repointed {reg.Command} → {exe}" +
                    (r.Ok ? "" : $" FAILED: {r.Message}"));
            }
            else mutations.Add($"{h.DisplayName}: registration already correct");
        }
        return (mutations, problems);
    }

    private static int CmdRepair(string exe)
    {
        var (mutations, problems) = RepairInstall(exe);
        Console.WriteLine("Inbrisk Repair\n");
        foreach (var m in mutations) Console.WriteLine($"  ✓ {m}");
        foreach (var p in problems) Console.WriteLine($"  ✗ {p}");
        if (mutations.Count == 0 && problems.Count == 0)
            Console.WriteLine("  nothing to repair");
        return problems.Count == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------ setup

    private static async Task<int> CmdSetup(string[] args, string exe,
        Func<Task<(bool, bool, bool, bool)>>? probes)
    {
        var nonInteractive = Has(args, "--non-interactive") || Has(args, "--quiet") ||
            Has(args, "/quiet") || Has(args, "/s");
        var hostFilter = Opt(args, "--host");
        var exePath = File.Exists(exe) ? exe : InstallLayout.ProcessExePath;

        Console.WriteLine("Inbrisk Setup\n");
        Console.WriteLine("  ✓ Windows supported");
        if (probes != null)
        {
            var (uia, capture, input, _) = await probes();
            Console.WriteLine($"  {(uia ? "✓" : "✗")} UI Automation");
            Console.WriteLine($"  {(capture ? "✓" : "✗")} Capture backend");
            Console.WriteLine($"  {(input ? "✓" : "✗")} Input backend");
        }
        Console.WriteLine("  ✓ Emergency stop: Ctrl+Alt+Pause (resume: Ctrl+Alt+Shift+Pause)\n");

        var all = HostRegistry.All();
        Console.WriteLine("Detected MCP hosts:\n");
        foreach (var h in all)
        {
            var d = h.Detect();
            Console.WriteLine($"  {(d.Installed ? "✓" : "○")} {h.DisplayName}" +
                (d.Installed ? "" : " — not detected"));
        }

        var targets = all.Where(h => h.Detect().Installed).ToList();
        if (hostFilter != null)
        {
            var one = HostRegistry.Find(hostFilter);
            if (one == null)
            { Console.WriteLine($"unknown host '{hostFilter}'"); return 2; }
            targets = new List<IMcpHostAdapter> { one };
        }
        else if (!nonInteractive && targets.Count > 0)
        {
            targets = PickHosts(targets, exePath);
        }

        Console.WriteLine();
        var failures = 0;
        foreach (var h in targets)
        {
            var r = h.Connect(exePath);
            Console.WriteLine($"  {(r.Ok ? "✓" : "✗")} {h.DisplayName}: {r.Message}");
            if (!r.Ok) { failures++; Console.WriteLine(h.ManualSetup(exePath)); }
        }

        // verification: real handshake against the binary hosts will launch
        Console.WriteLine("\nVerifying MCP handshake…");
        var self = await McpSelfTest.RunAsync(exePath);
        Console.WriteLine(self.Ok
            ? $"  ✓ handshake ok — {self.ToolCount} tools, instructions present"
            : $"  ✗ handshake failed: {self.Error}");

        if (targets.Any(h => h.RequiresRestart))
            Console.WriteLine("\nRestart the configured host(s) — Inbrisk tools appear automatically.");
        return failures == 0 && self.Ok ? 0 : 1;
    }

    /// <summary>Host picker. Interactive console → arrow-key checklist with
    /// a "manual config" row; piped stdin → numbered line picker.</summary>
    private static List<IMcpHostAdapter> PickHosts(List<IMcpHostAdapter> detected,
        string exePath)
    {
        if (!Console.IsInputRedirected && Environment.UserInteractive)
        {
            try { return InteractivePicker(detected, exePath); }
            catch { /* console vanished mid-render — fall through */ }
        }
        return LinePicker(detected);
    }

    /// <summary>Numbered picker: type numbers (1,3), Enter = all, n = none.
    /// Accepts names/ids too ("cursor, devin").</summary>
    private static List<IMcpHostAdapter> LinePicker(List<IMcpHostAdapter> detected)
    {
        Console.WriteLine("\nConnect Inbrisk to:");
        for (var i = 0; i < detected.Count; i++)
        {
            var reg = detected[i].GetInbriskEntry();
            Console.WriteLine($"  [{i + 1}] {detected[i].DisplayName}" +
                (reg != null ? "  (already configured)" : ""));
        }
        Console.WriteLine("  [m] none — show me the manual MCP config instead");
        Console.Write("numbers or names (Enter = all, n = none, m = manual): ");
        var line = Console.ReadLine();
        if (line == null || line.Trim() == "") return detected;
        var t = line.Trim();
        if (t.Equals("m", StringComparison.OrdinalIgnoreCase))
        {
            PrintManualGuide(detected, InstallLayout.CanonicalExePath);
            return new List<IMcpHostAdapter>();
        }
        if (t.Equals("n", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("none", StringComparison.OrdinalIgnoreCase))
            return new List<IMcpHostAdapter>();
        var wanted = t.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return detected.Where(h => wanted.Any(w =>
                int.TryParse(w, out var n) && n >= 1 && n <= detected.Count
                    ? detected[n - 1] == h
                    : h.Id.StartsWith(w, StringComparison.OrdinalIgnoreCase) ||
                      h.DisplayName.StartsWith(w, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>Arrow-key checklist: ↑/↓ move, Space toggles, Enter confirms.
    /// Last row is "manual" — selecting it prints the guide and connects
    /// nothing. All detected hosts start checked.</summary>
    private static List<IMcpHostAdapter> InteractivePicker(
        List<IMcpHostAdapter> detected, string exePath)
    {
        const string ManualRow = "✎  none — show me the manual MCP config instead";
        var rows = detected.Count + 1;
        var checked_ = detected.Select(_ => true).ToArray();
        var cursor = 0;

        Console.WriteLine("\nConnect Inbrisk to:   (↑/↓ move · Space toggle · Enter confirm)");
        var top = Console.CursorTop;

        void Render()
        {
            Console.SetCursorPosition(0, top);
            for (var i = 0; i < rows; i++)
            {
                var line = i < detected.Count
                    ? $"  {(checked_[i] ? "[x]" : "[ ]")} {detected[i].DisplayName}" +
                      (detected[i].GetInbriskEntry() != null ? "  (already configured)" : "")
                    : $"      {ManualRow}";
                line = (i == cursor ? "›" : " ") + line;
                Console.WriteLine(line.PadRight(Console.BufferWidth - 1));
            }
        }

        Render();
        while (true)
        {
            var key = Console.ReadKey(intercept: true).Key;
            switch (key)
            {
                case ConsoleKey.UpArrow:   cursor = (cursor - 1 + rows) % rows; break;
                case ConsoleKey.DownArrow: cursor = (cursor + 1) % rows; break;
                case ConsoleKey.Spacebar when cursor < detected.Count:
                    checked_[cursor] = !checked_[cursor]; break;
                case ConsoleKey.Enter:
                    Console.WriteLine();
                    if (cursor == detected.Count)
                    {
                        PrintManualGuide(detected, exePath);
                        return new List<IMcpHostAdapter>();
                    }
                    return detected.Where((_, i) => checked_[i]).ToList();
                case ConsoleKey.Escape:
                    Console.WriteLine();
                    return new List<IMcpHostAdapter>();
            }
            Render();
        }
    }

    /// <summary>§33 manual setup page: canonical launch command, generic JSON,
    /// per-host config paths, verification steps. String form so the setup
    /// app can render the same guide.</summary>
    public static string ManualGuideText(string exePath)
    {
        var exeJson = exePath.Replace("\\", "\\\\");
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($$"""

            ── Manual MCP setup ────────────────────────────────────────

            Inbrisk speaks MCP over stdio. The host launches:

              "{{exePath}}" mcp

            Generic config (most hosts use this exact shape):

              {
                "mcpServers": {
                  "inbrisk": {
                    "command": "{{exeJson}}",
                    "args": ["mcp"]
                  }
                }
              }

            Known config locations on this machine:
            """);
        foreach (var h in HostRegistry.All())
        {
            if (h is JsonFileHostAdapter j)
                sb.AppendLine($"              {h.DisplayName,-15} {j.ConfigPath}  (key: {j.RootKey})");
        }
        sb.AppendLine("""

            Steps:
              1. Add the entry under your host's MCP server key.
              2. Restart the host (or start a new session).
              3. Ask it to list MCP tools — computer_observe should appear.
              4. Run `inbrisk doctor` to verify the handshake end-to-end.

            Note: VS Code uses "servers" instead of "mcpServers" and needs
            "type": "stdio" on the entry.

            Emergency stop once connected: Ctrl+Alt+Pause
            """);
        return sb.ToString();
    }

    private static void PrintManualGuide(List<IMcpHostAdapter> detected, string exePath)
        => Console.WriteLine(ManualGuideText(exePath));

    private static bool Prompt(string question)
    {
        if (!Environment.UserInteractive) return false;
        Console.Write($"{question} [y/N] ");
        var key = Console.ReadLine();
        return key?.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>InbriskSetup.exe entry: install then onboard — in-process so
    /// a double-clicked console gets the real interactive picker. The host
    /// configs point at the canonical installed path regardless of where the
    /// setup exe was launched from.</summary>
    public static async Task<int> RunSetupExeAsync(string[] args,
        Func<Task<(bool, bool, bool, bool)>>? probes)
    {
        var quiet = args.Any(a => a is "/quiet" or "/s" or "--quiet" or "-q");
        var machine = args.Any(a => a is "--machine" or "/machine");

        Console.WriteLine("Inbrisk Setup\n");
        var inst = SelfInstaller.Install(machine);
        Console.WriteLine($"  {(inst.Ok ? "✓" : "✗")} {inst.Message}");

        var code = 1;
        if (inst.Ok)
        {
            var installedExe = Path.Combine(inst.InstallDir, InstallLayout.ExeName);
            code = await CmdSetup(
                quiet ? ["setup", "--non-interactive"] : ["setup"],
                installedExe, probes);
            if (code == 0)
            {
                Console.WriteLine("\nInbrisk is ready.");
                Console.WriteLine("Emergency Stop: Ctrl + Alt + Pause");
                Console.WriteLine($"Run `{installedExe} doctor` anytime to check health.");
            }
        }

        // double-clicked setup must not vanish — keep the console up
        if (!quiet && !Console.IsInputRedirected && Environment.UserInteractive)
        {
            Console.WriteLine("\nPress any key to exit…");
            try { Console.ReadKey(intercept: true); } catch { }
        }
        return code;
    }

    public sealed class UsageException(string message) : Exception(message);
}
