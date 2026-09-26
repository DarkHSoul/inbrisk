using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Inbrisk.Tests;

/// <summary>
/// Real MCP protocol tests: a genuine MCP client (official SDK) connects to
/// the Inbrisk.Mcp server over stdio as a child process. Nothing bypasses
/// the JSON-RPC transport — initialize/tools-call/disconnect all go over
/// the wire.
/// </summary>
[Collection("desktop")]
public sealed class McpTests
{
    private readonly DesktopFixture _fx;
    public McpTests(DesktopFixture fx) => _fx = fx;

    private static async Task<McpClient> ConnectAsync()
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "inbrisk-mcp.dll");
        Assert.True(File.Exists(dll), $"inbrisk-mcp.dll missing at {dll}");
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = "dotnet",
            Arguments = [dll],
            // isolate from any real Inbrisk instance on this machine: a
            // distinct chord + marker means we never collide with or follow
            // a production server's emergency state
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["INBRISK_PANIC_HOTKEY"] = "Ctrl+Alt+F12",
                ["INBRISK_RESUME_HOTKEY"] = "Ctrl+Alt+Shift+F12",
                ["INBRISK_EMERGENCY_STATE"] = Path.Combine(Path.GetTempPath(),
                    $"inbrisk-mcptest-{Guid.NewGuid():N}.flag"),
            },
        });
        return await McpClient.CreateAsync(transport);
    }

    private static string Text(CallToolResult r)
        => string.Join("\n", r.Content.OfType<TextContentBlock>().Select(t => t.Text));

    private static JsonDocument Json(CallToolResult r)
        => JsonDocument.Parse(Text(r));

    [Fact]
    public async Task Initialize_And_ToolsList()
    {
        await using var client = await ConnectAsync();
        Assert.Equal("inbrisk", client.ServerInfo.Name);
        Assert.False(string.IsNullOrEmpty(client.ServerInfo.Version));

        var tools = await client.ListToolsAsync();
        var names = tools.Select(t => t.Name).ToHashSet();
        foreach (var expected in new[]
        {
            "computer_observe", "computer_windows", "computer_inspect",
            "computer_find", "computer_click", "computer_invoke",
            "computer_set_value", "computer_type", "computer_key",
            "computer_hotkey", "computer_scroll", "computer_drag",
            "computer_focus_window", "computer_wait", "computer_wait_for",
            "computer_wait_for_change", "computer_wait_for_stable",
            "computer_screenshot", "computer_run", "computer_reset_input",
            "computer_save_recipe", "computer_run_recipe", "computer_list_recipes",
            "computer_screen_memory", "computer_scroll_into_view",
            "computer_pause_run", "computer_resume_run",
        })
            Assert.Contains(expected, names);

        var res = await client.ListResourcesAsync();
        Assert.Contains(res, r => r.Uri == "inbrisk://capabilities");
        var cap = await client.ReadResourceAsync("inbrisk://capabilities");
        Assert.Contains("elementIds", cap.Contents.OfType<TextResourceContents>()
            .First().Text);

        var prompts = await client.ListPromptsAsync();
        Assert.Contains(prompts, p => p.Name == "inbrisk-usage");

        // the screen-control indicator is a user trust surface — no MCP tool
        // may hide, disable, or reconfigure it
        Assert.DoesNotContain(names, n =>
            n.Contains("indicator", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("border", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("overlay", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Observe_IsSemanticFirst()
    {
        await using var client = await ConnectAsync();
        var r = await client.CallToolAsync("computer_observe",
            new Dictionary<string, object?> { ["hwnd"] = $"0x{_fx.Hwnd:X}" });
        var text = Text(r);
        Assert.False(r.IsError);
        Assert.Contains("active window:", text);
        Assert.Contains("elements (", text);
        Assert.Contains("[uia_", text);          // canonical element ids
        Assert.DoesNotContain(r.Content, c => c is ImageContentBlock); // auto: no pixels needed
    }

    [Fact]
    public async Task TestApp_Mcp_EndToEnd()
    {
        await using var client = await ConnectAsync();
        var hwnd = $"0x{_fx.Hwnd:X}";

        // 1. observe
        var obs = await client.CallToolAsync("computer_observe",
            new Dictionary<string, object?> { ["hwnd"] = hwnd });
        Assert.False(obs.IsError);

        // 2. find the textbox and set its value over MCP
        var findTb = await client.CallToolAsync("computer_find",
            new Dictionary<string, object?>
            { ["hwnd"] = hwnd, ["role"] = "edit", ["name"] = "text" });
        var tbText = Text(findTb);
        Assert.False(findTb.IsError);
        var tbId = System.Text.RegularExpressions.Regex
            .Match(tbText, @"\[(uia_\d+_\d+)\]").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(tbId));

        // set with bounded retries — the shared TestApp textbox can hold
        // leftover focus/input from earlier suite tests, so verify the
        // committed value via inspect before saving
        var committed = false;
        for (var attempt = 0; attempt < 3 && !committed; attempt++)
        {
            await client.CallToolAsync("computer_set_value",
                new Dictionary<string, object?>
                { ["elementId"] = tbId, ["value"] = "" });
            var setv = await client.CallToolAsync("computer_set_value",
                new Dictionary<string, object?>
                { ["elementId"] = tbId, ["value"] = "hello-mcp" });
            using var setDoc = Json(setv);
            Assert.False(setv.IsError == true, Text(setv));
            Assert.Equal("Verified", setDoc.RootElement.GetProperty("status").GetString());
            Assert.Equal("ValueReadback",
                setDoc.RootElement.GetProperty("evidence").GetProperty("method").GetString());
            var insp = await client.CallToolAsync("computer_inspect",
                new Dictionary<string, object?> { ["elementId"] = tbId });
            committed = Text(insp).Contains("value = hello-mcp");
        }
        Assert.True(committed, "textbox never committed hello-mcp");

        // 3. find + toggle the checkbox
        var findCb = await client.CallToolAsync("computer_find",
            new Dictionary<string, object?>
            { ["hwnd"] = hwnd, ["role"] = "checkbox" });
        var cbId = System.Text.RegularExpressions.Regex
            .Match(Text(findCb), @"\[(uia_\d+_\d+)\]").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(cbId));
        var tog = await client.CallToolAsync("computer_click",
            new Dictionary<string, object?> { ["elementId"] = cbId });
        Assert.False(tog.IsError == true, Text(tog));

        // 4. find + invoke Save
        var findSave = await client.CallToolAsync("computer_find",
            new Dictionary<string, object?>
            { ["hwnd"] = hwnd, ["role"] = "button", ["name"] = "Save" });
        var saveId = System.Text.RegularExpressions.Regex
            .Match(Text(findSave), @"\[(uia_\d+_\d+)\]").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(saveId));
        var inv = await client.CallToolAsync("computer_invoke",
            new Dictionary<string, object?> { ["elementId"] = saveId });
        Assert.False(inv.IsError == true, Text(inv));

        // 5. state file proves the click actually landed in TestApp
        var deadline = DateTime.Now.AddSeconds(5);
        var state = "";
        while (DateTime.Now < deadline)
        {
            if (File.Exists(DesktopFixture.StateFile))
                state = DesktopFixture.ReadState() ?? "";
            if (state.Contains("saved:hello-mcp")) break;
            await Task.Delay(150);
        }
        Assert.Contains("saved:hello-mcp", state);
    }

    [Fact]
    public async Task SemanticTarget_TypeAppend_ReturnsPostState()
    {
        await using var client = await ConnectAsync();

        // one call: semantic target → resolve → foreground → set → verify
        var r = await client.CallToolAsync("computer_type",
            new Dictionary<string, object?>
            {
                ["target"] = new Dictionary<string, object?>
                {
                    ["window"] = "InbriskTestApp",
                    ["role"] = "edit",
                    ["name"] = "text",
                },
                ["text"] = "seed",
                ["mode"] = "replace",
            });
        Assert.False(r.IsError == true, Text(r));
        using var doc = Json(r);
        var root = doc.RootElement;
        Assert.Equal("Verified", root.GetProperty("status").GetString());
        Assert.Equal("ValueReadback",
            root.GetProperty("evidence").GetProperty("method").GetString());
        var post = root.GetProperty("post");
        Assert.Equal("Edit", post.GetProperty("role").GetString());
        Assert.Equal("seed", post.GetProperty("value").GetString());
        Assert.True(root.GetProperty("changes")
            .GetProperty("valueChanged").GetBoolean());

        // append through a process-scoped target — must read back the tail
        r = await client.CallToolAsync("computer_type",
            new Dictionary<string, object?>
            {
                ["target"] = new Dictionary<string, object?>
                {
                    ["process"] = "Inbrisk.TestApp",
                    ["role"] = "edit",
                },
                ["text"] = "+tail",
                ["mode"] = "append",
            });
        Assert.False(r.IsError == true, Text(r));
        using var doc2 = Json(r);
        Assert.Equal("Verified", doc2.RootElement.GetProperty("status").GetString());
        Assert.EndsWith("+tail",
            doc2.RootElement.GetProperty("post").GetProperty("value").GetString());
    }

    [Fact]
    public async Task RunPlan_Batch_Bindings_Checkpoint_Resume_Pause()
    {
        await using var client = await ConnectAsync();
        var hwnd = $"0x{_fx.Hwnd:X}";
        DesktopFixture.WriteState("idle");

        // one MCP call: find → bind $editor → set_value via $ref → assert →
        // checkpoint hands control back before the save
        var r = await client.CallToolAsync("computer_run",
            new Dictionary<string, object?>
            {
                ["steps"] = new object?[]
                {
                    new Dictionary<string, object?>
                    {
                        ["action"] = "find",
                        ["target"] = new Dictionary<string, object?>
                            { ["hwnd"] = hwnd, ["role"] = "edit", ["name"] = "text" },
                        ["as"] = "editor",
                    },
                    new Dictionary<string, object?>
                    {
                        ["action"] = "set_value",
                        ["elementId"] = "$editor",
                        ["value"] = "run-plan",
                    },
                    new Dictionary<string, object?>
                    {
                        ["action"] = "assert",
                        ["elementId"] = "$editor",
                        ["contains"] = "run-plan",
                    },
                    new Dictionary<string, object?>
                    {
                        ["action"] = "checkpoint",
                        ["note"] = "before save",
                    },
                },
            });
        using var doc = Json(r);
        var root = doc.RootElement;
        Assert.Equal("Checkpoint", root.GetProperty("status").GetString());
        var runId = root.GetProperty("runId").GetString();
        Assert.False(string.IsNullOrEmpty(runId));
        Assert.Equal(3, root.GetProperty("executed").GetInt32());
        Assert.Equal(3, root.GetProperty("pause").GetProperty("step").GetInt32());
        Assert.StartsWith("uia_",
            root.GetProperty("bindings").GetProperty("editor").GetString());
        Assert.True(root.GetProperty("pause")
            .TryGetProperty("observationDelta", out _));
        var stepsArr = root.GetProperty("steps");
        Assert.Equal("Verified",
            stepsArr[1].GetProperty("status").GetString()); // set_value verified

        // resume the same run — bindings survive across calls
        var r2 = await client.CallToolAsync("computer_run",
            new Dictionary<string, object?>
            {
                ["runId"] = runId,
                ["steps"] = new object?[]
                {
                    new Dictionary<string, object?>
                    {
                        ["action"] = "invoke",
                        ["target"] = new Dictionary<string, object?>
                            { ["hwnd"] = hwnd, ["role"] = "button", ["name"] = "Save" },
                    },
                    new Dictionary<string, object?>
                        { ["action"] = "wait", ["ms"] = 400 },
                },
            });
        using var doc2 = Json(r2);
        Assert.Equal("Completed", doc2.RootElement.GetProperty("status").GetString());
        Assert.Equal(2, doc2.RootElement.GetProperty("executed").GetInt32());
        Assert.True(doc2.RootElement.GetProperty("internalActions").GetInt32() >= 2);

        var deadline = DateTime.Now.AddSeconds(5);
        var state = "";
        while (DateTime.Now < deadline)
        {
            state = DesktopFixture.ReadState() ?? "";
            if (state.Contains("saved:run-plan")) break;
            await Task.Delay(150);
        }
        Assert.Contains("saved:run-plan", state);

        // a step that cannot resolve pauses with recovery context — the run
        // does not blindly continue
        var r3 = await client.CallToolAsync("computer_run",
            new Dictionary<string, object?>
            {
                ["runId"] = runId,
                ["steps"] = new object?[]
                {
                    new Dictionary<string, object?>
                    {
                        ["action"] = "click",
                        ["target"] = new Dictionary<string, object?>
                            { ["hwnd"] = hwnd, ["role"] = "button",
                              ["name"] = "NoSuchButtonXYZ" },
                    },
                    new Dictionary<string, object?>
                        { ["action"] = "wait", ["ms"] = 10 },
                },
            });
        using var doc3 = Json(r3);
        var root3 = doc3.RootElement;
        Assert.Equal("Paused", root3.GetProperty("status").GetString());
        var pause = root3.GetProperty("pause");
        Assert.Equal(0, pause.GetProperty("step").GetInt32());
        Assert.True(pause.TryGetProperty("observationDelta", out _));
        Assert.True(pause.TryGetProperty("availableElements", out var avail));
        Assert.True(avail.GetArrayLength() > 0);
        // the second step never ran — the wait step stayed un-executed
        Assert.Equal(0, root3.GetProperty("executed").GetInt32());
    }

    [Fact]
    public async Task Cancellation_Propagates_To_LongWait()
    {
        await using var client = await ConnectAsync();
        using var cts = new CancellationTokenSource();
        var call = client.CallToolAsync("computer_wait",
            new Dictionary<string, object?> { ["ms"] = 30000 }, cancellationToken: cts.Token);
        await Task.Delay(400);
        cts.Cancel();
        var sw = Stopwatch.StartNew();
        try { await call; } catch (OperationCanceledException) { /* client-side cancel */ }
        Assert.True(sw.ElapsedMilliseconds < 5000,
            $"cancel took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task Structured_Stale_Error()
    {
        await using var client = await ConnectAsync();
        var r = await client.CallToolAsync("computer_invoke",
            new Dictionary<string, object?> { ["elementId"] = "uia_999999" });
        Assert.True(r.IsError);
        var text = Text(r);
        Assert.True(text.Contains("TargetNotFound") || text.Contains("Stale")
            || text.Contains("Malformed"),
            $"expected structured error, got: {text}");
    }

    [Fact]
    public async Task Screenshot_Returns_ImageBlock()
    {
        await using var client = await ConnectAsync();
        var r = await client.CallToolAsync("computer_screenshot",
            new Dictionary<string, object?>
            { ["target"] = "window", ["hwnd"] = $"0x{_fx.Hwnd:X}" });
        Assert.False(r.IsError);
        Assert.Contains(r.Content, c => c is ImageContentBlock);
        Assert.Contains("frameId=", Text(r));
        var frameId = long.Parse(System.Text.RegularExpressions.Regex
            .Match(Text(r), @"frameId=(\d+)").Groups[1].Value);
        var obsId = long.Parse(System.Text.RegularExpressions.Regex
            .Match(Text(r), @"observationId=(\d+)").Groups[1].Value);
        var mismatch = await client.CallToolAsync("computer_click",
            new Dictionary<string, object?>
            {
                ["frameId"] = frameId, ["observationId"] = obsId + 1,
                ["x"] = 10, ["y"] = 10,
            });
        Assert.True(mismatch.IsError);
        Assert.Contains("StaleFrame", Text(mismatch));
    }
}
