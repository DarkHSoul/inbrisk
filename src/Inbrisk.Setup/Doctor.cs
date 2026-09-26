using System.Text.Json;

namespace Inbrisk.Setup;

/// <summary>
/// `inbrisk doctor` — read-only diagnostics. Checks are grouped like the
/// spec's example output; nothing here mutates machine state. Problems point
/// at `inbrisk repair`.
/// </summary>
public static class Doctor
{
    public sealed record Check(bool Ok, string Name, string? Detail = null);
    public sealed record Section(string Title, List<Check> Checks);

    public static async Task<List<Section>> RunAsync(string exePath,
        Func<Task<(bool uia, bool capture, bool input, bool dpi)>>? probes = null)
    {
        var sections = new List<Section>();

        // ---- Installation ----
        var inst = new List<Check>();
        inst.Add(new(true, $"Version: {InstallLayout.Version}"));
        // informational — a release built outside git legitimately has none
        inst.Add(new(true, "Build commit", InstallLayout.BuildCommit ?? "unknown"));
        var sha = InstallLayout.SelfSha256();
        inst.Add(new(sha != "unavailable", "Binary SHA-256", sha));
        inst.Add(new(InstallLayout.IsInstalled, "Install path",
            InstallLayout.IsInstalled
                ? InstallLayout.ProcessDir
                : $"running unpacked: {InstallLayout.ProcessDir}"));
        var metaPath = Path.Combine(InstallLayout.ProcessDir, "install-metadata.json");
        if (InstallLayout.IsInstalled)
            inst.Add(new(File.Exists(metaPath), "Install metadata",
                File.Exists(metaPath) ? metaPath : "missing — run inbrisk repair"));
        sections.Add(new("Installation", inst));

        // ---- Windows capability probes (supplied by the CLI — it owns the
        // runtime; doctor itself stays a lightweight read-only surface) ----
        var win = new List<Check>();
        win.Add(new(Environment.OSVersion.Version.Major >= 10,
            "Supported Windows", Environment.OSVersion.ToString()));
        if (probes != null)
        {
            var p = await probes();
            win.Add(new(p.uia, "UI Automation"));
            win.Add(new(p.capture, "Capture backend"));
            win.Add(new(p.input, "SendInput backend"));
            win.Add(new(p.dpi, "DPI awareness"));
        }
        sections.Add(new("Windows", win));

        // ---- Safety ----
        var safe = new List<Check>();
        var em = EmergencyProbe.Read();
        safe.Add(new(!em.StopMarkerPresent, "Emergency state",
            em.StopMarkerPresent ? "STOPPED (marker present — Ctrl+Alt+Shift+Pause to resume)"
                         : "no pending stop marker"));
        // No live authority while other inbrisk processes exist is the
        // deadlock signature: every new MCP session registers, finds the
        // chord taken and no peer to delegate to → permanent EmergencyStopped.
        safe.Add(new(em.StopMarkerPresent || em.AuthorityAlive ||
                     em.MutexAuthority || em.InbriskProcessCount <= 1,
            "Panic authority", em.Detail));
        safe.Add(new(true, "Emergency hotkeys", "Ctrl+Alt+Pause stop / Ctrl+Alt+Shift+Pause resume"));
        sections.Add(new("Safety", safe));

        // ---- MCP self test: real subprocess handshake ----
        var mcp = new List<Check>();
        var self = await McpSelfTest.RunAsync(exePath);
        mcp.Add(new(self.Ok && self.StdoutClean, "stdio launch + handshake",
            self.Ok ? $"{self.ServerName} {self.Version}, {self.ToolCount} tools"
                    : self.Error));
        if (self.Ok)
        {
            mcp.Add(new(self.HasComputerRun, "computer_run available"));
            mcp.Add(new(self.InstructionsPresent, "server instructions present"));
            mcp.Add(new(self.StdoutClean, "stdout protocol-clean"));
            mcp.Add(new(self.CleanDisconnect, "clean disconnect, no orphan"));
        }
        sections.Add(new("MCP", mcp));

        // ---- Hosts ----
        var hosts = new List<Check>();
        foreach (var h in HostRegistry.All())
        {
            var d = h.Detect();
            if (!d.Installed) { hosts.Add(new(true, $"{h.DisplayName}", "not installed")); continue; }
            HostRegistration? reg = null;
            try { reg = h.GetInbriskEntry(); }
            catch (ConfigMalformedException e)
            {
                hosts.Add(new(false, h.DisplayName, $"MALFORMED config: {e.ConfigPath}"));
                continue;
            }
            hosts.Add(reg == null
                ? new(true, h.DisplayName, "installed, not configured — `inbrisk connect " + h.Id + "`")
                : new(reg.PointsAt(exePath) && File.Exists(reg.Command),
                    h.DisplayName,
                    reg.PointsAt(exePath)
                        ? $"configured ({reg.ConfigPath})"
                        : $"STALE entry → {reg.Command} (run inbrisk repair)"));
        }
        sections.Add(new("Hosts", hosts));

        // ---- Indicator / update ----
        var misc = new List<Check>();
        misc.Add(new(true, "Indicator lifecycle",
            "border renders only during live MCP sessions (Disconnected = none)"));
        misc.Add(new(!File.Exists(InstallLayout.PendingUpdateMarker),
            "Pending update",
            File.Exists(InstallLayout.PendingUpdateMarker)
                ? "staged update waiting for idle" : "none"));
        sections.Add(new("State", misc));

        return sections;
    }

    public static void Print(List<Section> sections)
    {
        Console.WriteLine("Inbrisk Doctor\n");
        var anyFail = false;
        foreach (var s in sections)
        {
            Console.WriteLine(s.Title);
            foreach (var c in s.Checks)
            {
                if (!c.Ok) anyFail = true;
                Console.WriteLine($"  {(c.Ok ? "✓" : "✗")} {c.Name}" +
                    (c.Detail != null ? $" — {c.Detail}" : ""));
            }
            Console.WriteLine();
        }
        if (anyFail)
            Console.WriteLine("Some checks failed — run `inbrisk repair` to fix registration/install issues.");
    }

    public static string ToJson(List<Section> sections) =>
        JsonSerializer.Serialize(new
        {
            version = InstallLayout.Version,
            buildCommit = InstallLayout.BuildCommit,
            codeSha256 = InstallLayout.SelfSha256(),
            sections = sections.Select(s => new
            {
                title = s.Title,
                checks = s.Checks.Select(c => new { c.Name, ok = c.Ok, c.Detail }),
            }),
            ok = sections.SelectMany(s => s.Checks).All(c => c.Ok),
        }, new JsonSerializerOptions { WriteIndented = true });
}
