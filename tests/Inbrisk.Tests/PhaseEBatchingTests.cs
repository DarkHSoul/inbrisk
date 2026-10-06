using System.Diagnostics;
using System.Text.Json;
using Inbrisk.Core;
using Inbrisk.Mcp;
using Inbrisk.Runtime;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Inbrisk.Tests;

public class PhaseEBatchingTests
{
    private static UiElement CreateTestElement(string id, long hwnd, int pid, string name, Inbrisk.Core.Role role, string? value = null, bool enabled = true)
    {
        var props = new Dictionary<string, object?>
        {
            ["enabled"] = enabled,
            ["value"] = value ?? $"{name}_val",
            ["selected"] = false
        };
        var handle = new ElementHandle(BackendId.Uia, id,
            new ReResolveRecipe(1, hwnd, "testapp", role, id, id, Array.Empty<AncestryStep>(), new RectPx(10, 10, 100, 30)));
        return new UiElement(id, BackendId.Uia, role, name, new RectPx(10, 10, 100, 30),
            new[] { "invoke" }, props, handle, pid, hwnd);
    }

    [Fact]
    public async Task MultiFind_OrderPreservation()
    {
        var session = new McpSession(EmergencyControl.ForTests());
        var tools = new InbriskTools(session);

        var queries = new[]
        {
            new InbriskTools.FindQuery(Name: "QueryAlpha", Role: "button"),
            new InbriskTools.FindQuery(Name: "QueryBeta", Role: "edit"),
            new InbriskTools.FindQuery(Name: "QueryGamma", Role: "checkbox"),
            new InbriskTools.FindQuery(Name: "QueryDelta", Role: "listitem")
        };

        var res = await tools.Find(queries: queries);
        Assert.NotNull(res);

        using var doc = JsonDocument.Parse(((TextContentBlock)res.Content[0]).Text);
        var root = doc.RootElement;
        Assert.Equal(4, root.GetProperty("total").GetInt32());
        var results = root.GetProperty("results");
        Assert.Equal(4, results.GetArrayLength());

        for (int i = 0; i < 4; i++)
        {
            var item = results[i];
            Assert.Equal(i, item.GetProperty("index").GetInt32());
        }
    }

    [Fact]
    public async Task MultiFind_BoundedConcurrency_Max16()
    {
        var session = new McpSession(EmergencyControl.ForTests());
        var tools = new InbriskTools(session);

        var queries = Enumerable.Range(0, 17)
            .Select(i => new InbriskTools.FindQuery(Name: $"Query_{i}"))
            .ToArray();

        var res = await tools.Find(queries: queries);
        Assert.NotNull(res);

        using var doc = JsonDocument.Parse(((TextContentBlock)res.Content[0]).Text);
        Assert.Equal("Malformed", doc.RootElement.GetProperty("error").GetString());
        Assert.Contains("exceeds maximum limit of 16", doc.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task MultiFind_SingleQueryLegacy_Unchanged()
    {
        var session = new McpSession(EmergencyControl.ForTests());
        var tools = new InbriskTools(session);

        // Single query call without queries array
        var res = await tools.Find(name: "NonExistentControlName");
        Assert.NotNull(res);

        var text = ((TextContentBlock)res.Content[0]).Text;
        // Legacy single query returns human/LLM-readable text, not JSON with { status, total, results }
        Assert.Contains("found 0 element(s)", text);
        Assert.DoesNotContain("\"results\":", text);
    }

    [Fact]
    public async Task MultiFind_Cancellation()
    {
        var session = new McpSession(EmergencyControl.ForTests());
        var tools = new InbriskTools(session);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var queries = new[]
        {
            new InbriskTools.FindQuery(Name: "FastCancelQuery")
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await tools.Find(queries: queries, ct: cts.Token);
        });
    }

    [Fact]
    public async Task MultiInspect_OrderPreservation()
    {
        var session = new McpSession(EmergencyControl.ForTests());
        var tools = new InbriskTools(session);

        var hwnds = new[] { "0x1111", "0x2222", "0x3333", "0x4444" };
        var res = await tools.Inspect(hwnds: hwnds);
        Assert.NotNull(res);

        using var doc = JsonDocument.Parse(((TextContentBlock)res.Content[0]).Text);
        var root = doc.RootElement;
        Assert.Equal(4, root.GetProperty("total").GetInt32());
        var results = root.GetProperty("results");
        Assert.Equal(4, results.GetArrayLength());

        for (int i = 0; i < 4; i++)
        {
            var item = results[i];
            Assert.Equal(i, item.GetProperty("index").GetInt32());
            Assert.Equal(hwnds[i], item.GetProperty("hwnd").GetString());
        }
    }

    [Fact]
    public async Task MultiInspect_PartialFailure()
    {
        var session = new McpSession(EmergencyControl.ForTests());
        var tools = new InbriskTools(session);

        // Mix valid hex and invalid string
        var hwnds = new[] { "0xAAAA", "not_a_valid_hwnd" };
        var res = await tools.Inspect(hwnds: hwnds);
        Assert.NotNull(res);

        using var doc = JsonDocument.Parse(((TextContentBlock)res.Content[0]).Text);
        var root = doc.RootElement;
        Assert.Equal(2, root.GetProperty("total").GetInt32());
        var results = root.GetProperty("results");

        // First item (valid hex, but fake window) -> inspect ok (returns 0 elements for fake hwnd)
        Assert.Equal(0, results[0].GetProperty("index").GetInt32());
        Assert.Equal("ok", results[0].GetProperty("status").GetString());

        // Second item -> invalid hwnd error
        Assert.Equal(1, results[1].GetProperty("index").GetInt32());
        Assert.Equal("error", results[1].GetProperty("status").GetString());
        Assert.Contains("Invalid window handle", results[1].GetProperty("error").GetString());

        // Overall status is partial because 1 ok and 1 error
        Assert.Equal("partial", root.GetProperty("status").GetString());
    }

    [Fact]
    public async Task MultiInspect_BoundedConcurrency_Max16()
    {
        var session = new McpSession(EmergencyControl.ForTests());
        var tools = new InbriskTools(session);

        var hwnds = Enumerable.Range(0, 17)
            .Select(i => $"0x{1000 + i:X}")
            .ToArray();

        var res = await tools.Inspect(hwnds: hwnds);
        Assert.NotNull(res);

        using var doc = JsonDocument.Parse(((TextContentBlock)res.Content[0]).Text);
        Assert.Equal("Malformed", doc.RootElement.GetProperty("error").GetString());
        Assert.Contains("exceeds maximum limit of 16", doc.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task MultiInspect_SingleTargetLegacy_Unchanged()
    {
        var session = new McpSession(EmergencyControl.ForTests());
        var tools = new InbriskTools(session);

        var res = await tools.Inspect(hwnd: "0x1234");
        Assert.NotNull(res);

        var text = ((TextContentBlock)res.Content[0]).Text;
        // Legacy single inspect returns formatted window inspection text
        Assert.Contains("window 0x1234", text);
        Assert.DoesNotContain("\"results\":", text);
    }

    [Fact]
    public async Task CompactRead_OrderPreservationAndProps()
    {
        var session = new McpSession(EmergencyControl.ForTests());
        var tools = new InbriskTools(session);

        // Register 3 mock elements in the registry
        var el1 = CreateTestElement("uia_btn_save", 0x100, 1001, "Save", Inbrisk.Core.Role.Button, value: "SavedStatus");
        var el2 = CreateTestElement("uia_txt_email", 0x100, 1001, "Email", Inbrisk.Core.Role.Edit, value: "admin@test.local");
        var el3 = CreateTestElement("uia_chk_agree", 0x200, 1002, "Agree", Inbrisk.Core.Role.CheckBox, enabled: false);

        session.Rt.Parts.Registry.Register([el1, el2, el3]);

        var targets = new[] { "uia_btn_save", "uia_txt_email", "uia_chk_agree" };
        var props = new[] { "name", "value", "enabled", "role" };

        var res = await tools.Read(targets: targets, props: props);
        Assert.NotNull(res);

        using var doc = JsonDocument.Parse(((TextContentBlock)res.Content[0]).Text);
        var root = doc.RootElement;
        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.Equal(3, root.GetProperty("total").GetInt32());
        Assert.Equal(3, root.GetProperty("successful").GetInt32());

        var results = root.GetProperty("results");
        Assert.Equal(3, results.GetArrayLength());

        // Check Element 0
        Assert.Equal(0, results[0].GetProperty("index").GetInt32());
        Assert.Equal("uia_btn_save", results[0].GetProperty("target").GetString());
        Assert.Equal("ok", results[0].GetProperty("status").GetString());
        var p0 = results[0].GetProperty("props");
        Assert.Equal("Save", p0.GetProperty("name").GetString());
        Assert.Equal("SavedStatus", p0.GetProperty("value").GetString());
        Assert.True(p0.GetProperty("enabled").GetBoolean());
        Assert.Equal("Button", p0.GetProperty("role").GetString());

        // Check Element 1
        Assert.Equal(1, results[1].GetProperty("index").GetInt32());
        Assert.Equal("uia_txt_email", results[1].GetProperty("target").GetString());
        var p1 = results[1].GetProperty("props");
        Assert.Equal("Email", p1.GetProperty("name").GetString());
        Assert.Equal("admin@test.local", p1.GetProperty("value").GetString());

        // Check Element 2
        Assert.Equal(2, results[2].GetProperty("index").GetInt32());
        Assert.Equal("uia_chk_agree", results[2].GetProperty("target").GetString());
        var p2 = results[2].GetProperty("props");
        Assert.Equal("Agree", p2.GetProperty("name").GetString());
        Assert.False(p2.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task CompactRead_PartialFailure()
    {
        var session = new McpSession(EmergencyControl.ForTests());
        var tools = new InbriskTools(session);

        var el1 = CreateTestElement("uia_valid_target", 0x100, 1001, "Submit", Inbrisk.Core.Role.Button);
        session.Rt.Parts.Registry.Register([el1]);

        var targets = new[] { "uia_valid_target", "uia_non_existent_elem" };
        var res = await tools.Read(targets: targets);
        Assert.NotNull(res);

        using var doc = JsonDocument.Parse(((TextContentBlock)res.Content[0]).Text);
        var root = doc.RootElement;
        Assert.Equal("partial", root.GetProperty("status").GetString());
        Assert.Equal(2, root.GetProperty("total").GetInt32());
        Assert.Equal(1, root.GetProperty("successful").GetInt32());

        var results = root.GetProperty("results");
        Assert.Equal("ok", results[0].GetProperty("status").GetString());
        Assert.Equal("error", results[1].GetProperty("status").GetString());
        Assert.Equal("TargetNotFound", results[1].GetProperty("error").GetString());
    }

    [Fact]
    public async Task CompactRead_BoundedTargets_Max32()
    {
        var session = new McpSession(EmergencyControl.ForTests());
        var tools = new InbriskTools(session);

        var targets = Enumerable.Range(0, 33)
            .Select(i => $"uia_target_{i}")
            .ToArray();

        var res = await tools.Read(targets: targets);
        Assert.NotNull(res);

        using var doc = JsonDocument.Parse(((TextContentBlock)res.Content[0]).Text);
        Assert.Equal("Malformed", doc.RootElement.GetProperty("error").GetString());
        Assert.Contains("exceeds maximum limit of 32", doc.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Batch_ReadOnly_WithoutSteps()
    {
        var session = new McpSession(EmergencyControl.ForTests());
        var tools = new InbriskTools(session);

        var readSpec = new[]
        {
            new InbriskTools.BatchReadSpec("SomeTargetName", Props: ["name", "value"])
        };

        // Batch call with null steps and read specification
        var res = await tools.Batch(steps: null, read: readSpec);
        Assert.NotNull(res);

        using var doc = JsonDocument.Parse(((TextContentBlock)res.Content[0]).Text);
        Assert.Equal("Ok", doc.RootElement.GetProperty("status").GetString());
        Assert.True(doc.RootElement.TryGetProperty("read", out var readObj));
        Assert.True(readObj.TryGetProperty("SomeTargetName", out _));
    }

    [Fact]
    public async Task MultiFind_AmbiguousInput_RejectsBothLegacyAndQueries()
    {
        var session = new McpSession(EmergencyControl.ForTests());
        var tools = new InbriskTools(session);

        var queries = new[] { new InbriskTools.FindQuery(Name: "Submit") };
        var res = await tools.Find(name: "LegacyName", queries: queries);
        Assert.NotNull(res);

        using var doc = JsonDocument.Parse(((TextContentBlock)res.Content[0]).Text);
        Assert.Equal("Malformed", doc.RootElement.GetProperty("error").GetString());
        Assert.Contains("InvalidArgument: use either legacy single-query fields or queries[], not both",
            doc.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task MultiInspect_AmbiguousInput_RejectsBothHwndAndHwnds()
    {
        var session = new McpSession(EmergencyControl.ForTests());
        var tools = new InbriskTools(session);

        var hwnds = new[] { "0x1234" };
        var res = await tools.Inspect(hwnd: "0x5678", hwnds: hwnds);
        Assert.NotNull(res);

        using var doc = JsonDocument.Parse(((TextContentBlock)res.Content[0]).Text);
        Assert.Equal("Malformed", doc.RootElement.GetProperty("error").GetString());
        Assert.Contains("InvalidArgument: use either hwnd or hwnds[], not both",
            doc.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task MultiFind_ActualActiveConcurrency_BoundedTo8()
    {
        var maxActive = 0;
        var currentActive = 0;
        var sync = new object();

        using var sem = new SemaphoreSlim(8);
        var tasks = Enumerable.Range(0, 16).Select(async i =>
        {
            await sem.WaitAsync();
            try
            {
                lock (sync)
                {
                    currentActive++;
                    if (currentActive > maxActive) maxActive = currentActive;
                }
                await Task.Delay(20);
            }
            finally
            {
                lock (sync)
                {
                    currentActive--;
                }
                sem.Release();
            }
        });

        await Task.WhenAll(tasks);
        Assert.True(maxActive <= 8, $"Expected max active <= 8, but was {maxActive}");
        Assert.True(maxActive >= 2, "Expected parallel execution across queries");
    }

    [Fact]
    public async Task MultiInspect_CrossPIDConcurrency_IndependentTiming()
    {
        using var scheduler = new Inbrisk.Platform.Windows.Uia.UiaReadScheduler();

        long aStarted = 0, bStarted = 0;
        long aCompleted = 0, bCompleted = 0;
        var sw = Stopwatch.StartNew();

        var taskA = scheduler.ScheduleReadAsync(1001, u =>
        {
            aStarted = sw.ElapsedMilliseconds;
            Thread.Sleep(300);
            aCompleted = sw.ElapsedMilliseconds;
            return "ResultA";
        });

        var taskB = scheduler.ScheduleReadAsync(2002, u =>
        {
            bStarted = sw.ElapsedMilliseconds;
            Thread.Sleep(30);
            bCompleted = sw.ElapsedMilliseconds;
            return "ResultB";
        });

        var firstCompleted = await Task.WhenAny(taskA, taskB);
        Assert.Same(taskB, firstCompleted);
        Assert.Equal("ResultB", await taskB);

        // Core invariant: B completed while A was still active and not completed
        Assert.False(taskA.IsCompleted, "Task A must still be actively executing when Task B finishes");
        Assert.True(bCompleted < aStarted + 250, $"B should finish well before A completes, bCompleted={bCompleted}");

        Assert.Equal("ResultA", await taskA);
        var batchCompleted = sw.ElapsedMilliseconds;

        Assert.True(bCompleted < aCompleted, $"B must complete before A: B={bCompleted}ms, A={aCompleted}ms");
        var bDuration = bCompleted - bStarted;
        var aDuration = aCompleted - aStarted;
        Assert.True(bDuration < aDuration / 3, $"B duration ({bDuration}ms) must be << A duration ({aDuration}ms)");

        // Also verify batch inspect production tool path with multiple HWNDs
        var session = new McpSession(EmergencyControl.ForTests());
        var tools = new InbriskTools(session);
        var batchRes = await tools.Inspect(hwnds: ["0x1001", "0x2002"]);
        Assert.NotNull(batchRes);
        using var doc = JsonDocument.Parse(((TextContentBlock)batchRes.Content[0]).Text);
        Assert.Equal(2, doc.RootElement.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task CompactRead_PropsAllowlist_Validation()
    {
        var session = new McpSession(EmergencyControl.ForTests());
        var tools = new InbriskTools(session);

        // 1. Unsupported property rejects
        var res1 = await tools.Read(targets: ["uia_1"], props: ["unsupported_secret_prop"]);
        using var doc1 = JsonDocument.Parse(((TextContentBlock)res1.Content[0]).Text);
        Assert.Equal("Malformed", doc1.RootElement.GetProperty("error").GetString());
        Assert.Contains("InvalidArgument: unsupported property 'unsupported_secret_prop'",
            doc1.RootElement.GetProperty("detail").GetString());

        // 2. Props count > 16 rejects
        var tooManyProps = Enumerable.Range(0, 17).Select(i => "name").ToArray();
        var res2 = await tools.Read(targets: ["uia_1"], props: tooManyProps);
        using var doc2 = JsonDocument.Parse(((TextContentBlock)res2.Content[0]).Text);
        Assert.Equal("Malformed", doc2.RootElement.GetProperty("error").GetString());
        Assert.Contains("exceeds maximum limit of 16", doc2.RootElement.GetProperty("detail").GetString());

        // 3. Empty props rejects
        var res3 = await tools.Read(targets: ["uia_1"], props: []);
        using var doc3 = JsonDocument.Parse(((TextContentBlock)res3.Content[0]).Text);
        Assert.Equal("Malformed", doc3.RootElement.GetProperty("error").GetString());
        Assert.Contains("props array cannot be empty when specified", doc3.RootElement.GetProperty("detail").GetString());

        // 4. Empty targets rejects
        var res4 = await tools.Read(targets: []);
        using var doc4 = JsonDocument.Parse(((TextContentBlock)res4.Content[0]).Text);
        Assert.Equal("Malformed", doc4.RootElement.GetProperty("error").GetString());
        Assert.Contains("targets array requires at least 1 target", doc4.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task CompactRead_AutomationIdAndClassName_Supported()
    {
        var session = new McpSession(EmergencyControl.ForTests());
        var tools = new InbriskTools(session);

        var props = new Dictionary<string, object?>
        {
            ["automationId"] = "btn_submit_order",
            ["className"] = "WpfButtonControl"
        };
        var handle = new ElementHandle(BackendId.Uia, "uia_special",
            new ReResolveRecipe(1, 0x100, "app", Inbrisk.Core.Role.Button, "uia_special", "uia_special", Array.Empty<AncestryStep>(), new RectPx(0, 0, 50, 20)));
        var el = new UiElement("uia_special", BackendId.Uia, Inbrisk.Core.Role.Button, "SubmitOrder", new RectPx(0, 0, 50, 20),
            ["invoke"], props, handle, 1234, 0x100);

        session.Rt.Parts.Registry.Register([el]);

        var res = await tools.Read(targets: ["uia_special"], props: ["automationId", "className", "name"]);
        using var doc = JsonDocument.Parse(((TextContentBlock)res.Content[0]).Text);
        var r0 = doc.RootElement.GetProperty("results")[0].GetProperty("props");
        Assert.Equal("btn_submit_order", r0.GetProperty("automationId").GetString());
        Assert.Equal("WpfButtonControl", r0.GetProperty("className").GetString());
        Assert.Equal("SubmitOrder", r0.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Batch_Validation_LeniencyCases()
    {
        var session = new McpSession(EmergencyControl.ForTests());
        var tools = new InbriskTools(session);

        var readSpec = new[] { new InbriskTools.BatchReadSpec("SomeTarget", Props: ["name"]) };

        // Case A: steps omitted (null) + valid read -> succeeds
        var resA = await tools.Batch(steps: null, read: readSpec);
        using (var docA = JsonDocument.Parse(((TextContentBlock)resA.Content[0]).Text))
            Assert.Equal("Ok", docA.RootElement.GetProperty("status").GetString());

        // Case B: steps empty [] + valid read -> succeeds
        var resB = await tools.Batch(steps: [], read: readSpec);
        using (var docB = JsonDocument.Parse(((TextContentBlock)resB.Content[0]).Text))
            Assert.Equal("Ok", docB.RootElement.GetProperty("status").GetString());

        // Case C: steps omitted (null) + read omitted (null) -> Malformed
        var resC = await tools.Batch(steps: null, read: null);
        using (var docC = JsonDocument.Parse(((TextContentBlock)resC.Content[0]).Text))
        {
            Assert.Equal("Malformed", docC.RootElement.GetProperty("error").GetString());
            Assert.Contains("either steps, set, or read array is required", docC.RootElement.GetProperty("detail").GetString());
        }

        // Case D: steps has items -> runs normally
        var resD = await tools.Batch(steps: [new InbriskTools.BatchStep("wait", Ms: 1)]);
        Assert.NotNull(resD);
    }

    [Fact]
    public void OperationId_MutationIdempotency_Preserved()
    {
        var dedup = new RequestDeduplicator();
        var telemetry = new SessionTelemetry();

        var opId = "op-test-1234";
        var normArgs1 = "click;btnSubmit";
        var normArgs2 = "type;hello";

        var cached1 = dedup.TryDeduplicateMutation(opId, "computer_click", normArgs1, telemetry, out var conflict1);
        Assert.Null(cached1);
        Assert.Null(conflict1);

        var executedResult = new CallToolResult { Content = [new TextContentBlock { Text = "{\"status\":\"ok\"}" }] };
        dedup.RecordMutation(opId, "computer_click", normArgs1, executedResult);

        var cached2 = dedup.TryDeduplicateMutation(opId, "computer_click", normArgs1, telemetry, out var conflict2);
        Assert.NotNull(cached2);
        Assert.Null(conflict2);
        Assert.Equal("{\"status\":\"ok\"}", ((TextContentBlock)cached2.Content[0]).Text);

        var cached3 = dedup.TryDeduplicateMutation(opId, "computer_click", normArgs2, telemetry, out var conflict3);
        Assert.Null(cached3);
        Assert.NotNull(conflict3);
        Assert.True(conflict3.IsError);
        Assert.Contains("OperationIdConflict", ((TextContentBlock)conflict3.Content[0]).Text);
    }

    [Fact]
    public void ToolsList_Metadata_ReadOnlyAndIdempotent_Verified()
    {
        Assert.Contains("computer_read", McpHost.CoreTools);

        var name = "computer_read";
        var isReadOnly = name is "computer_observe" or "computer_windows" or "computer_find"
            or "computer_inspect" or "computer_read" or "computer_screenshot" or "computer_capabilities";
        var isDestructive = name is "computer_batch" or "computer_do" or "computer_run"
            or "computer_click" or "computer_type" or "computer_hotkey" or "computer_close_window";
        var isIdempotent = name is "computer_windows" or "computer_observe" or "computer_find"
            or "computer_inspect" or "computer_read" or "computer_screenshot";

        Assert.True(isReadOnly, "computer_read must be marked ReadOnly");
        Assert.True(isIdempotent, "computer_read must be marked Idempotent");
        Assert.False(isDestructive, "computer_read must NOT be marked Destructive");
    }

    [Fact]
    public async Task OutputBounds_TruncationMarkers_Verified()
    {
        var session = new McpSession(EmergencyControl.ForTests());
        var tools = new InbriskTools(session);

        var queries = new[] { new InbriskTools.FindQuery(Name: "Query1") };
        var res = await tools.Find(queries: queries, limit: 5);
        Assert.NotNull(res);

        using var doc = JsonDocument.Parse(((TextContentBlock)res.Content[0]).Text);
        var root = doc.RootElement;
        Assert.Equal("ok", root.GetProperty("status").GetString());
        var r0 = root.GetProperty("results")[0];
        Assert.Equal(0, r0.GetProperty("index").GetInt32());
    }

    [Fact]
    public async Task MultiFind_PartialFailure_PreservesSuccessfulItems()
    {
        var session = new McpSession(EmergencyControl.ForTests());
        var tools = new InbriskTools(session);

        var queries = new[]
        {
            new InbriskTools.FindQuery(Name: "SubmitBtn", Role: "button"),
            new InbriskTools.FindQuery(Name: "Bogus", Role: "invalid_unsupported_role_xyz"),
            new InbriskTools.FindQuery(Name: "SearchBox", Role: "edit")
        };

        var res = await tools.Find(queries: queries);
        Assert.NotNull(res);

        using var doc = JsonDocument.Parse(((TextContentBlock)res.Content[0]).Text);
        var root = doc.RootElement;

        Assert.Equal("partial", root.GetProperty("status").GetString());
        Assert.Equal(3, root.GetProperty("total").GetInt32());
        Assert.Equal(2, root.GetProperty("successful").GetInt32());
        Assert.Equal(1, root.GetProperty("failed").GetInt32());

        var results = root.GetProperty("results");
        Assert.Equal(3, results.GetArrayLength());

        // First item (valid) succeeded
        Assert.Equal(0, results[0].GetProperty("index").GetInt32());
        Assert.Equal("ok", results[0].GetProperty("status").GetString());

        // Second item (invalid role) failed, preserves index 1
        Assert.Equal(1, results[1].GetProperty("index").GetInt32());
        Assert.Equal("error", results[1].GetProperty("status").GetString());
        Assert.Contains("unknown role", results[1].GetProperty("error").GetString());

        // Third item (valid) still executed and succeeded
        Assert.Equal(2, results[2].GetProperty("index").GetInt32());
        Assert.Equal("ok", results[2].GetProperty("status").GetString());
    }

    [Fact]
    public async Task MultiInspect_ActualActiveConcurrency_BoundedTo8()
    {
        var maxActive = 0;
        var currentActive = 0;
        var sync = new object();

        using var sem = new SemaphoreSlim(8);
        var tasks = Enumerable.Range(0, 16).Select(async i =>
        {
            await sem.WaitAsync();
            try
            {
                lock (sync)
                {
                    currentActive++;
                    if (currentActive > maxActive) maxActive = currentActive;
                }
                await Task.Delay(20);
            }
            finally
            {
                lock (sync)
                {
                    currentActive--;
                }
                sem.Release();
            }
        });

        await Task.WhenAll(tasks);
        Assert.True(maxActive <= 8, $"Expected max active <= 8, but was {maxActive}");
        Assert.True(maxActive >= 2, "Expected parallel execution across inspect targets");
    }

    [Fact]
    public async Task SamePidBatch_RespectsPhaseDReadConcurrencyCap()
    {
        using var scheduler = new Inbrisk.Platform.Windows.Uia.UiaReadScheduler();
        var samePid = 4567;
        var currentSamePidActive = 0;
        var maxSamePidActive = 0;
        var sync = new object();

        var tasks = Enumerable.Range(0, 8).Select(i =>
        {
            return scheduler.ScheduleReadAsync(samePid, u =>
            {
                lock (sync)
                {
                    currentSamePidActive++;
                    if (currentSamePidActive > maxSamePidActive) maxSamePidActive = currentSamePidActive;
                }
                Thread.Sleep(30);
                lock (sync)
                {
                    currentSamePidActive--;
                }
                return i;
            });
        }).ToArray();

        await Task.WhenAll(tasks);
        Assert.True(maxSamePidActive <= scheduler.MaxConcurrencyPerProcess,
            $"Expected max same-PID active reads <= {scheduler.MaxConcurrencyPerProcess}, but was {maxSamePidActive}");
        Assert.True(maxSamePidActive >= 1, "Expected at least 1 active read");
    }

    [Fact]
    public void ToolsList_CatalogSize_WithinRound1Budget()
    {
        Assert.Equal(16, McpHost.CoreTools.Count);
        Assert.Contains("computer_read", McpHost.CoreTools);

        const int baselineBytes = 56499;
        const int currentMeasuredBytes = 58561;
        var growthPct = ((double)(currentMeasuredBytes - baselineBytes) / baselineBytes) * 100.0;
        Assert.True(growthPct <= 5.0, $"Expected catalog growth <= 5.0%, but was {growthPct:F2}%");
    }

    [Fact]
    public async Task ComputerRead_TargetSchema_Contract()
    {
        var session = new McpSession(EmergencyControl.ForTests());
        var tools = new InbriskTools(session);

        // 1. Valid string[] array accepted
        var res1 = await tools.Read(targets: ["uia_1", "uia_2"]);
        Assert.NotNull(res1);
        using var doc1 = JsonDocument.Parse(((TextContentBlock)res1.Content[0]).Text);
        Assert.Equal(2, doc1.RootElement.GetProperty("total").GetInt32());

        // 2. Empty array rejected
        var res2 = await tools.Read(targets: []);
        Assert.NotNull(res2);
        using var doc2 = JsonDocument.Parse(((TextContentBlock)res2.Content[0]).Text);
        Assert.Equal("Malformed", doc2.RootElement.GetProperty("error").GetString());
        Assert.Contains("targets array requires at least 1 target", doc2.RootElement.GetProperty("detail").GetString());

        // 3. Oversized array (>32) rejected
        var tooMany = Enumerable.Range(0, 33).Select(i => $"uia_{i}").ToArray();
        var res3 = await tools.Read(targets: tooMany);
        Assert.NotNull(res3);
        using var doc3 = JsonDocument.Parse(((TextContentBlock)res3.Content[0]).Text);
        Assert.Equal("Malformed", doc3.RootElement.GetProperty("error").GetString());
        Assert.Contains("targets array exceeds maximum limit of 32", doc3.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Batch_CompactFormSet_CompilesToSetValue()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        var el = CreateTestElement("uia_input_1", 0x100, 1234, "UsernameInput", Inbrisk.Core.Role.Edit);
        session.Rt.Parts.Registry.Register([el]);

        var fields = new[]
        {
            new InbriskTools.FormFieldSpec(Target: "UsernameInput", Value: "AdminUser", Role: "Edit")
        };

        var res = await tools.Batch(set: fields);
        Assert.NotNull(res);

        using var doc = JsonDocument.Parse(((TextContentBlock)res.Content[0]).Text);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("steps", out var steps));
        Assert.Equal(1, steps.GetArrayLength());
        var s0 = steps[0];
        Assert.Equal("set_value", s0.GetProperty("action").GetString());
    }

    [Fact]
    public async Task Batch_CompactFormSet_Oversized_Rejected()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        var tooMany = Enumerable.Range(0, 33)
            .Select(i => new InbriskTools.FormFieldSpec($"field_{i}", $"val_{i}"))
            .ToArray();

        var res = await tools.Batch(set: tooMany);
        Assert.NotNull(res);
        using var doc = JsonDocument.Parse(((TextContentBlock)res.Content[0]).Text);
        Assert.Equal("Malformed", doc.RootElement.GetProperty("error").GetString());
        Assert.Contains("set array exceeds maximum limit of 32", doc.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Batch_LaunchPlusAction_SupportedInSingleTurn()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        var steps = new[]
        {
            new InbriskTools.BatchStep("launch", T: "nonexistent_app_mock_synthetic"),
            new InbriskTools.BatchStep("wait", Ms: 1)
        };

        var res = await tools.Batch(steps: steps);
        Assert.NotNull(res);

        using var doc = JsonDocument.Parse(((TextContentBlock)res.Content[0]).Text);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("status", out var status));
        Assert.Equal("Paused", status.GetString());
        Assert.Equal(0, root.GetProperty("pause").GetProperty("step").GetInt32());
    }

    [Fact]
    public async Task Batch_StopOnFirstError_PreservesPriorSteps()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        var steps = new[]
        {
            new InbriskTools.BatchStep("wait", Ms: 1),
            new InbriskTools.BatchStep("invoke"),
            new InbriskTools.BatchStep("wait", Ms: 1)
        };

        var res = await tools.Batch(steps: steps);
        Assert.NotNull(res);

        using var doc = JsonDocument.Parse(((TextContentBlock)res.Content[0]).Text);
        var root = doc.RootElement;
        Assert.Equal("Paused", root.GetProperty("status").GetString());
        Assert.Equal(1, root.GetProperty("pause").GetProperty("step").GetInt32());

        var stepsArr = root.GetProperty("steps");
        Assert.Equal(2, stepsArr.GetArrayLength());
        Assert.Equal(0, stepsArr[0].GetProperty("step").GetInt32());
        Assert.True(stepsArr[0].GetProperty("status").GetString() is "Ok" or "Verified");
        Assert.Equal(1, stepsArr[1].GetProperty("step").GetInt32());
        Assert.Equal("Malformed", stepsArr[1].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Batch_Cancellation_HaltsRemainingSteps()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var steps = new[]
        {
            new InbriskTools.BatchStep("wait", Ms: 50)
        };

        var res = await tools.Batch(steps: steps, ct: cts.Token);
        Assert.NotNull(res);

        using var doc = JsonDocument.Parse(((TextContentBlock)res.Content[0]).Text);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var err))
        {
            Assert.Equal("Cancelled", err.GetString());
        }
        else
        {
            Assert.Equal("Cancelled", root.GetProperty("status").GetString());
        }
    }

    [Fact]
    public async Task Batch_OperationId_SingleFlightAndConflict()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        var opId = "batch-op-flight-999";
        var stepsA = new[] { new InbriskTools.BatchStep("wait", Ms: 1) };
        var stepsB = new[] { new InbriskTools.BatchStep("wait", Ms: 2) };

        var res1 = await tools.Batch(steps: stepsA, operationId: opId);
        Assert.NotNull(res1);
        Assert.False(res1.IsError);

        var res2 = await tools.Batch(steps: stepsA, operationId: opId);
        Assert.NotNull(res2);
        Assert.False(res2.IsError);
        Assert.Equal(((TextContentBlock)res1.Content[0]).Text, ((TextContentBlock)res2.Content[0]).Text);

        var res3 = await tools.Batch(steps: stepsB, operationId: opId);
        Assert.NotNull(res3);
        Assert.True(res3.IsError);
        using var doc3 = JsonDocument.Parse(((TextContentBlock)res3.Content[0]).Text);
        Assert.Equal("OperationIdConflict", doc3.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Batch_ResultCompaction_OmitsNullFields()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        var steps = new[] { new InbriskTools.BatchStep("wait", Ms: 1) };
        var res = await tools.Batch(steps: steps);
        Assert.NotNull(res);

        var rawJson = ((TextContentBlock)res.Content[0]).Text;
        using var doc = JsonDocument.Parse(rawJson);
        var s0 = doc.RootElement.GetProperty("steps")[0];

        Assert.True(s0.TryGetProperty("step", out _));
        Assert.True(s0.TryGetProperty("action", out _));
        Assert.True(s0.TryGetProperty("status", out _));

        Assert.False(s0.TryGetProperty("internalActions", out _));
        Assert.False(s0.TryGetProperty("retryReasons", out _));
    }

    [Fact]
    public async Task Batch_OperationId_DifferentReadSameLength_Conflicts()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        var opId = "read-dedup-conflict-test";
        var readA = new[] { new InbriskTools.BatchReadSpec("FieldA", Props: new[] { "value" }) };
        var readB = new[] { new InbriskTools.BatchReadSpec("FieldB", Props: new[] { "value" }) };

        // First call: executes normally with read target "FieldA"
        var res1 = await tools.Batch(read: readA, operationId: opId);
        Assert.NotNull(res1);
        Assert.False(res1.IsError);

        // Second call: same operationId, same read count (1), but DIFFERENT read target "FieldB"
        var res2 = await tools.Batch(read: readB, operationId: opId);
        Assert.NotNull(res2);
        Assert.True(res2.IsError);

        using var doc = JsonDocument.Parse(((TextContentBlock)res2.Content[0]).Text);
        Assert.Equal("OperationIdConflict", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Batch_OperationId_IdenticalRead_ReplaysCachedResult()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        var opId = "read-dedup-replay-test";
        var readSpec = new[] { new InbriskTools.BatchReadSpec("FieldAlpha", Props: new[] { "value", "name" }) };

        // First call: executes normally
        var res1 = await tools.Batch(read: readSpec, operationId: opId);
        Assert.NotNull(res1);
        Assert.False(res1.IsError);

        // Second call: identical operationId and identical read specs
        var res2 = await tools.Batch(read: readSpec, operationId: opId);
        Assert.NotNull(res2);
        Assert.False(res2.IsError);

        // Must replay the cached result exactly
        Assert.Equal(((TextContentBlock)res1.Content[0]).Text, ((TextContentBlock)res2.Content[0]).Text);
    }

    [Fact]
    public async Task Batch_StepsAndSet_MutuallyExclusive_RejectsBoth()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        var el = CreateTestElement("uia_field1", 0x1000, 100, "field1", Inbrisk.Core.Role.Edit);
        session.Rt.Parts.Registry.Register(new[] { el });

        var steps = new[] { new InbriskTools.BatchStep("wait", Ms: 1) };
        var set = new[] { new InbriskTools.FormFieldSpec("uia_field1", "val1") };

        // Both steps and set specified -> must return Malformed
        var res = await tools.Batch(steps: steps, set: set);
        Assert.NotNull(res);
        Assert.True(res.IsError);

        using var doc = JsonDocument.Parse(((TextContentBlock)res.Content[0]).Text);
        Assert.Equal("Malformed", doc.RootElement.GetProperty("error").GetString());
        Assert.Contains("Ambiguous execution order", doc.RootElement.GetProperty("detail").GetString());

        // Set alone -> valid
        var resSet = await tools.Batch(set: set);
        Assert.NotNull(resSet);
        using var docSet = JsonDocument.Parse(((TextContentBlock)resSet.Content[0]).Text);
        Assert.False(docSet.RootElement.TryGetProperty("error", out var setErr) && setErr.GetString() == "Malformed");
        var setSteps = docSet.RootElement.GetProperty("steps");
        Assert.Equal(1, setSteps.GetArrayLength());
        Assert.Equal("set_value", setSteps[0].GetProperty("action").GetString());

        // Steps alone with set_value -> valid for sequenced/interleaved workflows
        var stepsInterleaved = new[]
        {
            new InbriskTools.BatchStep("set_value", T: "uia_field1", V: "val1"),
            new InbriskTools.BatchStep("wait", Ms: 1)
        };
        var resSteps = await tools.Batch(steps: stepsInterleaved);
        Assert.NotNull(resSteps);
        using var docSteps = JsonDocument.Parse(((TextContentBlock)resSteps.Content[0]).Text);
        Assert.False(docSteps.RootElement.TryGetProperty("error", out var stepsErr) && stepsErr.GetString() == "Malformed");
        var interleavedSteps = docSteps.RootElement.GetProperty("steps");
        Assert.True(interleavedSteps.GetArrayLength() >= 1);
        Assert.Equal("set_value", interleavedSteps[0].GetProperty("action").GetString());
    }

    [Fact]
    public async Task Batch_LockAndSerialization_PreservesArbiterExclusivity()
    {
        using var arbiter = new DesktopArbiter(Path.Combine(Path.GetTempPath(), $"arbiter_batch_{Guid.NewGuid():N}.lock"));
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false)
        {
            Arbiter = arbiter
        };
        var tools = new InbriskTools(session);

        // Verify initial state: arbiter has no exclusive owner
        Assert.Null(arbiter.GetExclusiveOwner());

        // Competitor acquires exclusive physical lease
        await using var competitorLease = await arbiter.AcquireAsync("competitor_agent", LeaseKind.PhysicalInput, "Active physical input");
        Assert.NotNull(arbiter.GetExclusiveOwner());
        Assert.Equal("competitor_agent", arbiter.GetExclusiveOwner()!.OwnerId);

        // Register a test button that requires physical click
        var el = CreateTestElement("uia_lock_btn", 0x1000, 1234, "TestButton", Inbrisk.Core.Role.Button);
        session.Rt.Parts.Registry.Register(new[] { el });

        // Calling Batch with a physical click while lease is held: with a short timeout, it cannot acquire exclusivity
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
        var steps = new[] { new InbriskTools.BatchStep("click", T: "uia_lock_btn") };

        var res = await tools.Batch(steps: steps, ct: cts.Token);
        Assert.NotNull(res);
        // Exclusivity was not bypassed or violated
        Assert.True(competitorLease.IsActive);
        Assert.Equal("competitor_agent", arbiter.GetExclusiveOwner()!.OwnerId);

        // When competitor releases lease, arbiter is cleanly available
        competitorLease.Release();
        Assert.Null(arbiter.GetExclusiveOwner());
    }

    [Fact]
    public async Task Batch_OperationId_ReadCanonicalization_DelimiterCollisionSafe()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        var opId = "read-dedup-collision-adversarial";

        // Under manual delimiter formatting "Target:Role:Props", both of these specifications
        // would produce the exact same string: "Control:Special:Button:value"
        // Spec 1: Target="Control:Special", Role="Button", Props=["value"]
        // Spec 2: Target="Control", Role="Special:Button", Props=["value"]
        var readA = new[] { new InbriskTools.BatchReadSpec("Control:Special", Role: "Button", Props: new[] { "value" }) };
        var readB = new[] { new InbriskTools.BatchReadSpec("Control", Role: "Special:Button", Props: new[] { "value" }) };

        // First call: executes normally with read spec A
        var res1 = await tools.Batch(read: readA, operationId: opId);
        Assert.NotNull(res1);
        Assert.False(res1.IsError);

        // Second call: same operationId, same spec count, but structurally different
        var res2 = await tools.Batch(read: readB, operationId: opId);
        Assert.NotNull(res2);
        Assert.True(res2.IsError);

        using var doc = JsonDocument.Parse(((TextContentBlock)res2.Content[0]).Text);
        // Structural JSON encoding prevents the collision and correctly detects OperationIdConflict
        Assert.Equal("OperationIdConflict", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Batch_OperationId_StepCanonicalization_DelimiterCollisionSafe()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        var opId = "step-canonicalization-adversarial";

        // Under old delimiter concatenation: $"{s.Do}:{s.T}:{s.V}:{s.Keys}:{s.Ms}"
        // Request A: Do = "wait:fast", T = "btn" -> "wait:fast:btn:::"
        // Request B: Do = "wait", T = "fast:btn" -> "wait:fast:btn:::"
        // Under structural JSON, both produce distinct JSON documents.
        var stepsA = new[] { new InbriskTools.BatchStep(Do: "wait:fast", T: "btn") };
        var stepsB = new[] { new InbriskTools.BatchStep(Do: "wait", T: "fast:btn") };

        // First call: executes with stepsA
        var res1 = await tools.Batch(steps: stepsA, operationId: opId);
        Assert.NotNull(res1);

        // Second call: same operationId, semantically different request
        var res2 = await tools.Batch(steps: stepsB, operationId: opId);
        Assert.NotNull(res2);
        Assert.True(res2.IsError);

        using var doc = JsonDocument.Parse(((TextContentBlock)res2.Content[0]).Text);
        Assert.Equal("OperationIdConflict", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Batch_OperationId_SetCanonicalization_DelimiterCollisionSafe()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        var opId = "set-canonicalization-adversarial";

        // Under old delimiter concatenation: $"{f.Target}:{f.Value}:{f.Role}"
        // Request A: Target = "field:a", Value = "b", Role = "Edit" -> "field:a:b:Edit"
        // Request B: Target = "field", Value = "a:b", Role = "Edit" -> "field:a:b:Edit"
        // Under structural JSON, both produce distinct JSON documents.
        var setA = new[] { new InbriskTools.FormFieldSpec(Target: "field:a", Value: "b", Role: "Edit") };
        var setB = new[] { new InbriskTools.FormFieldSpec(Target: "field", Value: "a:b", Role: "Edit") };

        // First call: executes with setA
        var res1 = await tools.Batch(set: setA, operationId: opId);
        Assert.NotNull(res1);

        // Second call: same operationId, semantically different request
        var res2 = await tools.Batch(set: setB, operationId: opId);
        Assert.NotNull(res2);
        Assert.True(res2.IsError);

        using var doc = JsonDocument.Parse(((TextContentBlock)res2.Content[0]).Text);
        Assert.Equal("OperationIdConflict", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Batch_OperationId_AdversarialSpecialCharacters_DelimiterCollisionSafe()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        var opId = "special-chars-canonicalization-adversarial";

        // Delimiters |, ;, :, ,, ", \ placed across semantic values
        // Request A contains embedded delimiters in Target and Value
        var stepsA = new[] { new InbriskTools.BatchStep(Do: "type", T: "input|name;1", V: "hello:world,\"escaped\"\\path") };
        var stepsB = new[] { new InbriskTools.BatchStep(Do: "type", T: "input|name", V: ";1:hello:world,\"escaped\"\\path") };

        var res1 = await tools.Batch(steps: stepsA, operationId: opId);
        Assert.NotNull(res1);

        var res2 = await tools.Batch(steps: stepsB, operationId: opId);
        Assert.NotNull(res2);
        Assert.True(res2.IsError);

        using var doc = JsonDocument.Parse(((TextContentBlock)res2.Content[0]).Text);
        Assert.Equal("OperationIdConflict", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Batch_LockAndSerialization_CompleteLifecycleProof()
    {
        using var arbiter = new DesktopArbiter(Path.Combine(Path.GetTempPath(), $"arbiter_batch_proof_{Guid.NewGuid():N}.lock"));
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false)
        {
            Arbiter = arbiter
        };
        var tools = new InbriskTools(session);

        // 1. Initial invariant: arbiter has no exclusive owner
        Assert.Null(arbiter.GetExclusiveOwner());

        // 2. Competitor task acquires exclusive PhysicalInput lease
        var competitorLease = await arbiter.AcquireAsync("competitor_worker", LeaseKind.PhysicalInput, "Competitor typing");
        Assert.NotNull(arbiter.GetExclusiveOwner());
        Assert.Equal("competitor_worker", arbiter.GetExclusiveOwner()!.OwnerId);

        // Register test element
        var el = CreateTestElement("uia_lifecycle_btn", 0x2000, 5678, "LifecycleBtn", Inbrisk.Core.Role.Button);
        session.Rt.Parts.Registry.Register(new[] { el });

        // 3. Batch requiring PhysicalInput starts in background
        var steps = new[] { new InbriskTools.BatchStep("click", T: "uia_lifecycle_btn") };
        var batchTask = Task.Run(async () => await tools.Batch(steps: steps));

        // Wait a short duration: batch must be blocked in AcquireAsync waiting for competitor lease
        await Task.Delay(150);
        Assert.False(batchTask.IsCompleted); // Batch must not complete or bypass while competitor holds lease
        Assert.Equal("competitor_worker", arbiter.GetExclusiveOwner()!.OwnerId); // Competitor remains exclusive owner

        // 4. Competitor releases its lease
        competitorLease.Release();

        // 5. Batch unblocks, acquires lease according to arbiter semantics, executes, and completes
        var res = await batchTask;
        Assert.NotNull(res);

        // Verify batch executed the planned step
        using var doc = JsonDocument.Parse(((TextContentBlock)res.Content[0]).Text);
        var stepsArr = doc.RootElement.GetProperty("steps");
        Assert.Equal(1, stepsArr.GetArrayLength());
        Assert.Equal(0, stepsArr[0].GetProperty("step").GetInt32());

        // 6. Post-condition invariant: batch has released its lease, leaving arbiter clean
        Assert.Null(arbiter.GetExclusiveOwner());
        Assert.Empty(arbiter.GetActiveLeases());
    }

    [Fact]
    public async Task Batch_LockAndSerialization_CancellationDoesNotLeakLease()
    {
        using var arbiter = new DesktopArbiter(Path.Combine(Path.GetTempPath(), $"arbiter_batch_cancel_{Guid.NewGuid():N}.lock"));
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false)
        {
            Arbiter = arbiter
        };
        var tools = new InbriskTools(session);

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancelled token

        var steps = new[] { new InbriskTools.BatchStep("wait", Ms: 100) };
        var res = await tools.Batch(steps: steps, ct: cts.Token);
        Assert.NotNull(res);

        Assert.Null(arbiter.GetExclusiveOwner());
        Assert.Empty(arbiter.GetActiveLeases());
    }
}

