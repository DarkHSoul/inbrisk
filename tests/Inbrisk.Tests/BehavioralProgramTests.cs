using System.Diagnostics;
using System.Text.Json;
using Inbrisk.Core;
using Inbrisk.Mcp;
using Inbrisk.Runtime;
using ModelContextProtocol.Protocol;
using Xunit;
using Role = Inbrisk.Core.Role;

namespace Inbrisk.Tests;

public class BehavioralProgramTests
{
    private static InbriskTools.RunStep S(
        string action,
        InbriskTools.TargetSpec? target = null,
        string? elementId = null,
        string? @as = null,
        InbriskTools.ItemFilter? where = null,
        InbriskTools.RunStep[]? steps = null,
        int? maxItems = null,
        int? maxPages = null,
        string[]? stopOn = null,
        string[]? collect = null,
        string? text = null) =>
        new(Action: action, Target: target, ElementId: elementId, As: @as,
            Where: where, Steps: steps, MaxItems: maxItems, MaxPages: maxPages,
            StopOn: stopOn, Collect: collect, Text: text);

    [Fact]
    public void Scan_Validation_RequiresStepsOrCollect()
    {
        // Neither steps nor collect provided -> must fail validation
        var step = S("scan", target: new InbriskTools.TargetSpec(Role: "List", Name: "Songs"));
        
        // Use reflection to call private ValidateStep
        var method = typeof(InbriskTools).GetMethod("ValidateStep",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var err = (string?)method!.Invoke(null, new object[] { step });
        Assert.NotNull(err);
        Assert.Contains("requires at least 'steps' to execute or 'collect'", err);
    }

    [Fact]
    public void Scan_Validation_AcceptsValidConfiguration()
    {
        var step = S("scan",
            target: new InbriskTools.TargetSpec(Role: "List", Name: "Sonuçlar"),
            @as: "song",
            where: new InbriskTools.ItemFilter(StartsWith: "X", Role: "ListItem"),
            collect: new[] { "name", "value" },
            steps: new[]
            {
                new InbriskTools.RunStep(Action: "click", ElementId: "$song")
            },
            maxItems: 10,
            maxPages: 2,
            stopOn: new[] { "ambiguous_target", "unexpected_dialog" });

        var method = typeof(InbriskTools).GetMethod("ValidateStep",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var err = (string?)method!.Invoke(null, new object[] { step });
        Assert.Null(err);
    }

    [Fact]
    public void Scan_Validation_RecursivelyValidatesSubSteps()
    {
        var invalidSubStep = new InbriskTools.RunStep(Action: "non_existent_action");
        var step = S("scan",
            target: new InbriskTools.TargetSpec(Role: "List"),
            steps: new[] { invalidSubStep });

        var method = typeof(InbriskTools).GetMethod("ValidateStep",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var err = (string?)method!.Invoke(null, new object[] { step });
        Assert.NotNull(err);
        Assert.Contains("sub-step scan[0]: unknown plan action", err);
    }

    [Fact]
    public void ItemFilter_Matches_CriteriaCorrectly()
    {
        var method = typeof(InbriskTools).GetMethod("MatchesItemFilter",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var recipe = new ReResolveRecipe(1, 100, "App", Role.ListItem, "X-Song Title", null, Array.Empty<AncestryStep>(), new RectPx(0, 0, 10, 10));
        var el = new UiElement("el_1", BackendId.Uia, Role.ListItem, "X-Song Title",
            new RectPx(0, 0, 10, 10), new[] { "click" },
            new Dictionary<string, object?> { ["value"] = "Artist A", ["state"] = "selected" },
            new ElementHandle(BackendId.Uia, "ref", recipe), 1, 100);

        // 1. StartsWith match
        var filter1 = new InbriskTools.ItemFilter(StartsWith: "X-");
        var res1 = (bool)method!.Invoke(null, new object[] { el, filter1 })!;
        Assert.True(res1);

        // 2. StartsWith mismatch
        var filter2 = new InbriskTools.ItemFilter(StartsWith: "Y-");
        var res2 = (bool)method!.Invoke(null, new object[] { el, filter2 })!;
        Assert.False(res2);

        // 3. Contains on value
        var filter3 = new InbriskTools.ItemFilter(Contains: "Artist A");
        var res3 = (bool)method!.Invoke(null, new object[] { el, filter3 })!;
        Assert.True(res3);

        // 4. NotContains
        var filter4 = new InbriskTools.ItemFilter(NotContains: "Artist B");
        var res4 = (bool)method!.Invoke(null, new object[] { el, filter4 })!;
        Assert.True(res4);

        // 5. Role match
        var filter5 = new InbriskTools.ItemFilter(Role: "ListItem");
        var res5 = (bool)method!.Invoke(null, new object[] { el, filter5 })!;
        Assert.True(res5);

        // 6. Role mismatch
        var filter6 = new InbriskTools.ItemFilter(Role: "Button");
        var res6 = (bool)method!.Invoke(null, new object[] { el, filter6 })!;
        Assert.False(res6);
    }

    [Fact]
    public void SubstituteVariables_InterpolatesValuesCorrectly()
    {
        var method = typeof(InbriskTools).GetMethod("SubstituteVariables",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var state = new RunState { RunId = "test_run" };
        state.Bindings["item"] = "el_42";
        state.Bindings["item.name"] = "Bohemian Rhapsody";
        state.Bindings["item.value"] = "Queen";
        state.Bindings["item.role"] = "ListItem";
        state.Bindings["user"] = "Alice";

        // 1. Direct variable reference: $item
        var res1 = (string?)method!.Invoke(null, new object[] { "$item", state });
        Assert.Equal("el_42", res1);

        // 2. Direct property reference: $item.name
        var res2 = (string?)method!.Invoke(null, new object[] { "$item.name", state });
        Assert.Equal("Bohemian Rhapsody", res2);

        // 3. Embedded $item.name and $item.value in sentence
        var res3 = (string?)method!.Invoke(null, new object[] { "Now playing $item.name by $item.value ($item.role)", state });
        Assert.Equal("Now playing Bohemian Rhapsody by Queen (ListItem)", res3);

        // 4. Must not confuse $item with $item.name when both are in bindings and sentence
        var res4 = (string?)method!.Invoke(null, new object[] { "Element $item is $item.name", state });
        Assert.Equal("Element el_42 is Bohemian Rhapsody", res4);

        // 5. Must handle {{var}} syntax
        var res5 = (string?)method!.Invoke(null, new object[] { "Hello {{user}}, playing {{item.name}}", state });
        Assert.Equal("Hello Alice, playing Bohemian Rhapsody", res5);

        // 6. Unknown / unbound variable passes through safely
        var res6 = (string?)method!.Invoke(null, new object[] { "Unknown $foo and {{bar}}", state });
        Assert.Equal("Unknown $foo and {{bar}}", res6);
    }

    private static InbriskTools CreateToolsWithoutRuntime()
    {
        var session = (McpSession)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(McpSession));
        typeof(McpSession).GetField("<SessionId>k__BackingField",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(session, "test-session");
        var tools = (InbriskTools)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(InbriskTools));
        typeof(InbriskTools).GetField("_s",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(tools, session);
        return tools;
    }

    [Fact]
    public void ActionResult_Includes_VerificationHint_BasedOnOutcomeKind()
    {
        var tools = CreateToolsWithoutRuntime();

        var actionResultMethod = typeof(InbriskTools).GetMethod("ActionResult",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(actionResultMethod);

        var sw = Stopwatch.StartNew();
        var steps = new[] { new AgentAction(AgentActionKind.Click, ElementId: "btn_1") };

        // 1. OutcomeKind.Verified
        var outcomeVerified = new StepOutcome(OutcomeKind.Verified, true, "ClickPattern.Invoke", "clicked successfully", 10);
        var res1 = (CallToolResult)actionResultMethod!.Invoke(tools, new object?[] {
            steps, outcomeVerified, sw, null, null, null, null, false
        })!;
        Assert.NotNull(res1);
        var text1 = ((TextContentBlock)res1.Content[0]).Text;
        using (var doc1 = JsonDocument.Parse(text1))
        {
            Assert.True(doc1.RootElement.TryGetProperty("verificationHint", out var hint1));
            Assert.Contains("outcome is verified with concrete evidence", hint1.GetString());
            Assert.Contains("follow-up computer_observe is NOT needed", hint1.GetString());
        }

        // 2. OutcomeKind.ObservedChange
        var outcomeObserved = new StepOutcome(OutcomeKind.ObservedChange, true, "Click", "observed window event", 10);
        var res2 = (CallToolResult)actionResultMethod!.Invoke(tools, new object?[] {
            steps, outcomeObserved, sw, null, null, null, null, false
        })!;
        var text2 = ((TextContentBlock)res2.Content[0]).Text;
        using (var doc2 = JsonDocument.Parse(text2))
        {
            Assert.True(doc2.RootElement.TryGetProperty("verificationHint", out var hint2));
            Assert.Contains("a window event was observed", hint2.GetString());
            Assert.Contains("computer_find/computer_observe", hint2.GetString());
        }

        // 3. OutcomeKind.Unverified
        var outcomeUnverified = new StepOutcome(OutcomeKind.Unverified, true, "Click", "dispatched to input", 10);
        var res3 = (CallToolResult)actionResultMethod!.Invoke(tools, new object?[] {
            steps, outcomeUnverified, sw, null, null, null, null, false
        })!;
        var text3 = ((TextContentBlock)res3.Content[0]).Text;
        using (var doc3 = JsonDocument.Parse(text3))
        {
            Assert.True(doc3.RootElement.TryGetProperty("verificationHint", out var hint3));
            Assert.Contains("the action WAS dispatched to OS/UIA", hint3.GetString());
        }
    }

    [Fact]
    public void RunReport_Includes_VerificationHint_OnCompleted()
    {
        var tools = CreateToolsWithoutRuntime();

        var runReportMethod = typeof(InbriskTools).GetMethod("RunReport",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(runReportMethod);

        var state = new RunState { RunId = "r_test" };
        var sw = Stopwatch.StartNew();
        var steps = new List<Dictionary<string, object?>>();

        var res = (CallToolResult)runReportMethod!.Invoke(tools, new object?[] {
            state, "Completed", sw, steps, 0, 1, 0, -1, null, null, false, false, null, null, null
        })!;

        var text = ((TextContentBlock)res.Content[0]).Text;
        using var doc = JsonDocument.Parse(text);
        Assert.True(doc.RootElement.TryGetProperty("verificationHint", out var hint));
        Assert.Contains("plan execution completed successfully", hint.GetString());
        Assert.Contains("follow-up computer_observe is NOT needed", hint.GetString());
    }

    [Fact]
    public void RunReport_Includes_PauseDiagnosis_WhenPaused()
    {
        var tools = CreateToolsWithoutRuntime();

        var runReportMethod = typeof(InbriskTools).GetMethod("RunReport",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(runReportMethod);

        var state = new RunState { RunId = "r_diag" };
        var sw = Stopwatch.StartNew();
        var steps = new List<Dictionary<string, object?>>();

        var diagnosis = new TargetDiagnosis(
            Reason: "FilteredByPropertyOrRelation",
            Summary: "Target button 'Save' was eliminated by within container",
            SearchedScope: "Window 0x1234",
            CandidatesMatchedBase: 1,
            EliminatedBy: "within: '$dialog'",
            SuggestedAction: "check if target is inside container '$dialog'");

        var res = (CallToolResult)runReportMethod!.Invoke(tools, new object?[] {
            state, "Paused", sw, steps, 0, 0, 0, 0, "TargetNotFound", "element not found", false, false, null, null, diagnosis
        })!;

        var text = ((TextContentBlock)res.Content[0]).Text;
        using var doc = JsonDocument.Parse(text);
        Assert.True(doc.RootElement.TryGetProperty("pause", out var pause));
        Assert.True(pause.TryGetProperty("diagnosis", out var diag));
        Assert.Equal("FilteredByPropertyOrRelation", diag.GetProperty("Reason").GetString());
        Assert.Equal("within: '$dialog'", diag.GetProperty("EliminatedBy").GetString());
        Assert.Equal(1, diag.GetProperty("CandidatesMatchedBase").GetInt32());
    }

    [Fact]
    public void Error_Includes_Diagnosis_WhenPresent()
    {
        var errorMethod = typeof(InbriskTools).GetMethod("Error",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
            null, new[] { typeof(OutcomeKind), typeof(string), typeof(Stopwatch), typeof(TargetDiagnosis) }, null);
        Assert.NotNull(errorMethod);

        var diagnosis = new TargetDiagnosis(
            Reason: "ModalDialogBlocking",
            Summary: "Modal dialog 'Confirm' is blocking input",
            SuggestedAction: "dismiss modal first");

        var res = (CallToolResult)errorMethod!.Invoke(null, new object?[] {
            OutcomeKind.TargetNotFound, "element covered", Stopwatch.StartNew(), diagnosis
        })!;

        Assert.True(res.IsError);
        var text = ((TextContentBlock)res.Content[0]).Text;
        using var doc = JsonDocument.Parse(text);
        Assert.True(doc.RootElement.TryGetProperty("diagnosis", out var diag));
        Assert.Equal("ModalDialogBlocking", diag.GetProperty("Reason").GetString());
        Assert.Equal("dismiss modal first", diag.GetProperty("SuggestedAction").GetString());
    }

    [Fact]
    public void DiagnoseNotFound_WithFilteredCandidates_ReturnsFilterDiagnosis()
    {
        var tools = CreateToolsWithoutRuntime();

        var diagnoseMethod = typeof(InbriskTools).GetMethod("DiagnoseNotFound",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(diagnoseMethod);

        var recipe = new ReResolveRecipe(1, 100, "App", Role.Button, "Submit", null, Array.Empty<AncestryStep>(), new RectPx(0, 0, 10, 10));
        var candidate = new UiElement("btn_sub", BackendId.Uia, Role.Button, "Submit",
            new RectPx(0, 0, 10, 10), new[] { "click" },
            new Dictionary<string, object?> { ["value"] = "Submit Order", ["className"] = "WpfButton" },
            new ElementHandle(BackendId.Uia, "ref", recipe), 1, 100);

        // 1. Filtered by Within
        var targetWithin = new InbriskTools.TargetSpec(Role: "Button", Name: "Submit", Within: "$sidebar");
        var diag1 = (TargetDiagnosis)diagnoseMethod!.Invoke(tools, new object?[] {
            targetWithin, 1, null, new List<UiElement> { candidate }
        })!;

        Assert.NotNull(diag1);
        Assert.Equal("FilteredByPropertyOrRelation", diag1.Reason);
        Assert.Contains("within: '$sidebar'", diag1.EliminatedBy);
        Assert.Equal(1, diag1.CandidatesMatchedBase);
        Assert.Contains("check if target is inside container '$sidebar'", diag1.SuggestedAction);

        // 2. Filtered by Value
        var targetVal = new InbriskTools.TargetSpec(Role: "Button", Name: "Submit", Value: "Cancel Order");
        var diag2 = (TargetDiagnosis)diagnoseMethod!.Invoke(tools, new object?[] {
            targetVal, 1, null, new List<UiElement> { candidate }
        })!;

        Assert.NotNull(diag2);
        Assert.Equal("FilteredByPropertyOrRelation", diag2.Reason);
        Assert.Contains("value filter: expected 'Cancel Order'", diag2.EliminatedBy);
        Assert.Contains("candidate had 'Submit Order'", diag2.EliminatedBy);

        // 3. Filtered by ClassName
        var targetCls = new InbriskTools.TargetSpec(Role: "Button", Name: "Submit", ClassName: "CustomToolbarButton");
        var diag3 = (TargetDiagnosis)diagnoseMethod!.Invoke(tools, new object?[] {
            targetCls, 1, null, new List<UiElement> { candidate }
        })!;

        Assert.NotNull(diag3);
        Assert.Equal("FilteredByPropertyOrRelation", diag3.Reason);
        Assert.Contains("className filter: expected 'CustomToolbarButton'", diag3.EliminatedBy);
        Assert.Contains("candidate had 'WpfButton'", diag3.EliminatedBy);
    }

    [Fact]
    public void TargetSpec_Summary_FormatsRoleNameAndPropertiesCorrectly()
    {
        var spec1 = new InbriskTools.TargetSpec(Role: "Button", Name: "Save", Within: "SecondaryPanel");
        Assert.Equal("{role: Button, name: \"Save\", within: $SecondaryPanel}", spec1.Summary());

        var specEmpty = new InbriskTools.TargetSpec();
        Assert.Equal("{empty target}", specEmpty.Summary());
    }

    [Fact]
    public void DiagnoseNotFound_WhenNoElementsMatch_ReturnsNoElementMatched()
    {
        var tools = CreateToolsWithoutRuntime();

        var diagnoseMethod = typeof(InbriskTools).GetMethod("DiagnoseNotFound",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(diagnoseMethod);

        var target = new InbriskTools.TargetSpec(Role: "Button", Name: "NonExistentButton");
        var diag = (TargetDiagnosis)diagnoseMethod!.Invoke(tools, new object?[] {
            target, 0, null, null
        })!;

        Assert.NotNull(diag);
        Assert.Equal("NoElementMatched", diag.Reason);
        Assert.Contains("No element matched target spec", diag.Summary);
        Assert.Contains("computer_observe or computer_find", diag.SuggestedAction);
    }
}
