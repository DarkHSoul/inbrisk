using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Inbrisk.Setup;

/// <summary>
/// Spawns `<exe> mcp` as a real subprocess and performs an actual MCP
/// handshake over stdio with hand-rolled JSON-RPC — deliberately NOT the MCP
/// client SDK, so the test also proves stdout carries nothing but protocol
/// frames (banners/logs would fail the per-line JSON check).
/// </summary>
public static class McpSelfTest
{
    public sealed record Result(
        bool Ok, string? ServerName, string? Version, int ToolCount,
        bool HasComputerRun, bool InstructionsPresent, bool StdoutClean,
        bool CleanDisconnect, string? Error);

    public static readonly string[] MinimumTools =
    [
        "computer_observe", "computer_find", "computer_click",
        "computer_type", "computer_run", "computer_screenshot",
        "computer_reset_input",
    ];

    public static async Task<Result> RunAsync(string exePath,
        string args = "mcp", int timeoutMs = 30000, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(exePath, args)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };
        // isolate from any real Inbrisk on the machine: distinct marker +
        // chords mean we never observe or mutate production emergency state
        psi.Environment["INBRISK_PANIC_HOTKEY"] = "Ctrl+Alt+F12";
        psi.Environment["INBRISK_RESUME_HOTKEY"] = "Ctrl+Alt+Shift+F12";
        psi.Environment["INBRISK_EMERGENCY_STATE"] =
            Path.Combine(Path.GetTempPath(), $"inbrisk-selftest-{Guid.NewGuid():N}.flag");

        Process? proc = null;
        var stdoutClean = true;
        var protocol = new List<JsonDocument>();
        try
        {
            proc = Process.Start(psi)!;
            var stderrBuf = new StringBuilder();
            _ = Task.Run(async () =>
            {
                try
                {
                    while (await proc.StandardError.ReadLineAsync() is { } l)
                        stderrBuf.AppendLine(l);
                }
                catch { }
            }, ct);

            async Task<JsonDocument?> Request(string method, object? prms, int id)
            {
                var req = JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0", id, method, @params = prms ?? new { },
                });
                await proc.StandardInput.WriteLineAsync(req);
                await proc.StandardInput.FlushAsync();
                var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (DateTime.UtcNow < deadline)
                {
                    var line = await proc.StandardOutput.ReadLineAsync(ct);
                    if (line == null) return null; // EOF — server died
                    var t = line.Trim();
                    if (t.Length == 0) continue;
                    JsonDocument? doc = null;
                    try { doc = JsonDocument.Parse(t); }
                    catch { stdoutClean = false; continue; }
                    var root = doc.RootElement;
                    // notifications have no id — keep them, keep reading
                    if (!root.TryGetProperty("id", out var rid)) { protocol.Add(doc); continue; }
                    if (root.TryGetProperty("method", out _)) { protocol.Add(doc); continue; } // server request
                    if (rid.ValueKind == JsonValueKind.Number && rid.GetInt32() == id)
                        return doc;
                    protocol.Add(doc);
                }
                return null;
            }

            var init = await Request("initialize", new
            {
                protocolVersion = "2025-06-18",
                capabilities = new { },
                clientInfo = new { name = "inbrisk-selftest", version = "1.0" },
            }, 1);
            if (init == null)
                return Fail("no initialize response", proc);
            var result = init.RootElement.GetProperty("result");
            var serverName = result.GetProperty("serverInfo").GetProperty("name").GetString();
            var version = result.GetProperty("serverInfo").GetProperty("version").GetString();
            var instructions = result.TryGetProperty("instructions", out var ins)
                ? ins.GetString() : null;

            await proc.StandardInput.WriteLineAsync(
                "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            await proc.StandardInput.FlushAsync();

            var tools = await Request("tools/list", new { }, 2);
            var toolNames = tools?.RootElement.GetProperty("result")
                .GetProperty("tools").EnumerateArray()
                .Select(t => t.GetProperty("name").GetString() ?? "")
                .ToHashSet() ?? new HashSet<string>();
            var missing = MinimumTools.Where(t => !toolNames.Contains(t)).ToArray();

            // clean disconnect: close stdin, process must exit on its own
            proc.StandardInput.Close();
            var cleanExit = proc.WaitForExit(8000);
            if (!cleanExit) try { proc.Kill(); } catch { }

            if (serverName != "inbrisk")
                return Fail($"unexpected server identity '{serverName}'", proc);
            if (missing.Length > 0)
                return Fail($"missing tools: {string.Join(", ", missing)}", proc);
            if (!stdoutClean)
                return Fail("stdout contained non-JSON-RPC output", proc);
            if (!cleanExit)
                return Fail("server did not exit on stdin close (orphan risk)", proc);

            return new Result(true, serverName, version, toolNames.Count,
                toolNames.Contains("computer_run"),
                !string.IsNullOrEmpty(instructions), stdoutClean,
                cleanExit, null);
        }
        catch (Exception e)
        {
            return Fail(e.Message, proc);
        }
        finally
        {
            foreach (var d in protocol) d.Dispose();
            try { proc?.Dispose(); } catch { }
        }

        static Result Fail(string error, Process? p)
        {
            try { if (p is { HasExited: false }) p.Kill(); } catch { }
            return new Result(false, null, null, 0, false, false, false, false, error);
        }
    }
}
