using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Inbrisk.Core.WorldState;
using Inbrisk.Mcp;
using Inbrisk.Mcp.DynamicToolset;
using Inbrisk.Mcp.Telemetry;
using Inbrisk.Platform.Windows.Uia;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Inbrisk.Tests;

/// <summary>
/// Phase H Round 6 (Lane 6): Telemetry, Response Polish & Full Architecture Integration Smoke Tests.
/// Validates:
/// 1. Call-level telemetry capture (queueMs, executionMs, responseBytes, estimatedTokens, cacheHit, retryCount, taskId, lane waits).
/// 2. Task-level metrics aggregation (total calls, parallel vs sequential count, total durations, server-estimated model turns).
/// 3. Server-estimated model turn heuristic (requests arriving within ~50ms group into parallel turns; gaps represent thinking time).
/// 4. Strict privacy filtering (passwords, clipboard secrets, raw document content, auth tokens).
/// 5. Response diet formatting: default slim response is compact ({ ok, st, ev, post, changed, new, stateVersion, next }).
/// 6. Response diet formatting: debug detail preserves full bounds, session metadata, and diagnostics.
/// 7. ErrorReadyArguments: deterministic retry-ready arguments for TargetNotFound, Ambiguous, and Malformed errors.
/// 8. Final Architecture Smoke: holistic end-to-end integration across proxy, read/mutation lanes, stateVersion, profiles, dynamic toolsets, and telemetry.
/// </summary>
public sealed class PhaseHRound6TelemetryAndPolishTests
{
    // =========================================================================
    // 1. Call-Level Metrics Captured
    // =========================================================================

    [Fact]
    public void Telemetry_CallLevelMetricsCaptured()
    {
        var aggregator = new TaskTelemetryAggregator();
        var arrival = DateTimeOffset.UtcNow;
        var completion = arrival.AddMilliseconds(45);

        var metrics = new CallTelemetryMetrics
        {
            CallId = "call-001",
            TaskId = "task-alpha",
            ToolName = "computer_find",
            ArrivedAt = arrival,
            CompletedAt = completion,
            QueueMs = 12.5,
            ExecutionMs = 45.0,
            ResponseBytes = 1024,
            EstimatedTokens = 256,
            CacheHit = true,
            RetryCount = 1,
            ReadLaneWaitMs = 5.2,
            MutationLaneWaitMs = 0.0,
            Arguments = new Dictionary<string, object?>
            {
                ["query"] = "Save button",
                ["scopeHwnd"] = 0x1234
            }
        };

        aggregator.RecordCall(metrics);

        var calls = aggregator.GetCallsForTask("task-alpha");
        Assert.Single(calls);

        var recorded = calls[0];
        Assert.Equal("call-001", recorded.CallId);
        Assert.Equal("task-alpha", recorded.TaskId);
        Assert.Equal("computer_find", recorded.ToolName);
        Assert.Equal(arrival, recorded.ArrivedAt);
        Assert.Equal(completion, recorded.CompletedAt);
        Assert.Equal(12.5, recorded.QueueMs);
        Assert.Equal(45.0, recorded.ExecutionMs);
        Assert.Equal(1024, recorded.ResponseBytes);
        Assert.Equal(256, recorded.EstimatedTokens);
        Assert.True(recorded.CacheHit);
        Assert.Equal(1, recorded.RetryCount);
        Assert.Equal(5.2, recorded.ReadLaneWaitMs);
        Assert.Equal(0.0, recorded.MutationLaneWaitMs);
        Assert.Equal("Save button", recorded.Arguments["query"]?.ToString());
    }

    // =========================================================================
    // 2. Task-Level Metrics Aggregated
    // =========================================================================

    [Fact]
    public void Telemetry_TaskLevelMetricsAggregated()
    {
        var aggregator = new TaskTelemetryAggregator();
        var baseTime = DateTimeOffset.UtcNow;

        // Turn 1: 2 parallel calls arriving within 20ms of each other (delta <= 50ms)
        var call1 = new CallTelemetryMetrics
        {
            TaskId = "task-beta",
            ToolName = "computer_read",
            ArrivedAt = baseTime,
            CompletedAt = baseTime.AddMilliseconds(30),
            QueueMs = 10,
            ExecutionMs = 30,
            ReadLaneWaitMs = 5,
            MutationLaneWaitMs = 0,
            ResponseBytes = 500
        };

        var call2 = new CallTelemetryMetrics
        {
            TaskId = "task-beta",
            ToolName = "computer_find",
            ArrivedAt = baseTime.AddMilliseconds(20),
            CompletedAt = baseTime.AddMilliseconds(40),
            QueueMs = 15,
            ExecutionMs = 20,
            ReadLaneWaitMs = 8,
            MutationLaneWaitMs = 0,
            ResponseBytes = 300
        };

        // Turn 2: Sequential call arriving at +500ms (gap > 50ms, represents thinking time)
        var call3 = new CallTelemetryMetrics
        {
            TaskId = "task-beta",
            ToolName = "computer_click",
            ArrivedAt = baseTime.AddMilliseconds(500),
            CompletedAt = baseTime.AddMilliseconds(550),
            QueueMs = 5,
            ExecutionMs = 50,
            ReadLaneWaitMs = 0,
            MutationLaneWaitMs = 12,
            ResponseBytes = 200
        };

        // Turn 3: Sequential call arriving at +1000ms
        var call4 = new CallTelemetryMetrics
        {
            TaskId = "task-beta",
            ToolName = "computer_observe",
            ArrivedAt = baseTime.AddMilliseconds(1000),
            CompletedAt = baseTime.AddMilliseconds(1040),
            QueueMs = 8,
            ExecutionMs = 40,
            ReadLaneWaitMs = 6,
            MutationLaneWaitMs = 0,
            ResponseBytes = 800
        };

        aggregator.RecordCall(call1);
        aggregator.RecordCall(call2);
        aggregator.RecordCall(call3);
        aggregator.RecordCall(call4);

        var summary = aggregator.GetTaskSummary("task-beta");

        Assert.Equal("task-beta", summary.TaskId);
        Assert.Equal(4, summary.TotalToolCalls);
        Assert.Equal(2, summary.ParallelCallCount); // call1 and call2
        Assert.Equal(2, summary.SequentialCallCount); // call3 and call4
        Assert.Equal(3, summary.EstimatedModelTurns);

        Assert.Equal(10 + 15 + 5 + 8, summary.TotalQueueWaitMs);
        Assert.Equal(5 + 8 + 0 + 6, summary.TotalReadLaneWaitMs);
        Assert.Equal(0 + 0 + 12 + 0, summary.TotalMutationLaneWaitMs);
        Assert.Equal(30 + 20 + 50 + 40, summary.TotalExecutionMs);
        Assert.True(summary.TotalThinkingMs > 0, "Thinking duration must be strictly positive across turns.");
    }

    // =========================================================================
    // 3. Model Turn Heuristic Groups Parallel Calls
    // =========================================================================

    [Fact]
    public void Telemetry_ModelTurnHeuristicGroupsParallelCalls()
    {
        var baseTime = DateTimeOffset.UtcNow;

        // Turn 1: 3 parallel calls arriving within 50ms (T=0ms, T=15ms, T=35ms)
        var turn1Calls = new[]
        {
            new CallTelemetryMetrics
            {
                CallId = "t1-c1",
                ArrivedAt = baseTime,
                CompletedAt = baseTime.AddMilliseconds(50)
            },
            new CallTelemetryMetrics
            {
                CallId = "t1-c2",
                ArrivedAt = baseTime.AddMilliseconds(15),
                CompletedAt = baseTime.AddMilliseconds(60)
            },
            new CallTelemetryMetrics
            {
                CallId = "t1-c3",
                ArrivedAt = baseTime.AddMilliseconds(35),
                CompletedAt = baseTime.AddMilliseconds(70)
            }
        };

        // Turn 2: 2 parallel calls after a 400ms thinking gap (arriving at T=470ms, T=490ms)
        var turn2Calls = new[]
        {
            new CallTelemetryMetrics
            {
                CallId = "t2-c1",
                ArrivedAt = baseTime.AddMilliseconds(470),
                CompletedAt = baseTime.AddMilliseconds(510)
            },
            new CallTelemetryMetrics
            {
                CallId = "t2-c2",
                ArrivedAt = baseTime.AddMilliseconds(490),
                CompletedAt = baseTime.AddMilliseconds(520)
            }
        };

        // Turn 3: 1 sequential call after a 500ms thinking gap (arriving at T=1020ms)
        var turn3Call = new CallTelemetryMetrics
        {
            CallId = "t3-c1",
            ArrivedAt = baseTime.AddMilliseconds(1020),
            CompletedAt = baseTime.AddMilliseconds(1060)
        };

        var allCalls = turn1Calls.Concat(turn2Calls).Append(turn3Call).ToList();

        var turns = TaskTelemetryAggregator.EstimateModelTurns(allCalls, arrivalThresholdMs: 50.0);

        Assert.Equal(3, turns.Count);

        // Turn 1 assertions
        Assert.Equal(1, turns[0].TurnIndex);
        Assert.Equal(3, turns[0].Calls.Count);
        Assert.True(turns[0].IsParallel);
        Assert.Equal(0, turns[0].ThinkingMs);

        // Turn 2 assertions (gap: 470 - 70 = 400ms thinking time)
        Assert.Equal(2, turns[1].TurnIndex);
        Assert.Equal(2, turns[1].Calls.Count);
        Assert.True(turns[1].IsParallel);
        Assert.InRange(turns[1].ThinkingMs, 395, 405);

        // Turn 3 assertions (gap: 1020 - 520 = 500ms thinking time)
        Assert.Equal(3, turns[2].TurnIndex);
        Assert.Single(turns[2].Calls);
        Assert.False(turns[2].IsParallel);
        Assert.InRange(turns[2].ThinkingMs, 495, 505);
    }

    // =========================================================================
    // 4. Privacy Filter Strictly Sanitizes Secrets
    // =========================================================================

    [Fact]
    public void Telemetry_PrivacySanitizesSecrets()
    {
        var rawArguments = new Dictionary<string, object?>
        {
            ["password"] = "SuperSecretP@ssword123!",
            ["pwd"] = "AdminRoot2026",
            ["authToken"] = "ghp_1234567890abcdef1234567890abcdef",
            ["bearer"] = "Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozS6-m1U_n0",
            ["apiKey"] = "sk-proj-999988887777666655554444333322221111",
            ["clipboard"] = "secret_clipboard_payload_with_key=abc1234567890",
            ["document"] = "CONFIDENTIAL INTERNAL FINANCIAL REPORT CONTENT",
            ["normalField"] = "public_button_name",
            ["connectionUrl"] = "https://user:password=Secret123@example.com/api"
        };

        var sanitized = TaskTelemetryAggregator.SanitizeArguments(rawArguments);

        // Verify password sanitization
        Assert.Equal("[REDACTED_PASSWORD]", sanitized["password"]);
        Assert.Equal("[REDACTED_PASSWORD]", sanitized["pwd"]);

        // Verify auth token & API key sanitization
        Assert.Equal("[REDACTED_AUTH_TOKEN]", sanitized["authToken"]);
        Assert.Equal("[REDACTED_AUTH_TOKEN]", sanitized["apiKey"]);
        Assert.Equal("[REDACTED_AUTH_TOKEN]", sanitized["bearer"]);

        // Verify clipboard and document content sanitization
        Assert.Equal("[REDACTED_CLIPBOARD]", sanitized["clipboard"]);
        Assert.Equal("[REDACTED_DOCUMENT]", sanitized["document"]);

        // Verify normal field is preserved
        Assert.Equal("public_button_name", sanitized["normalField"]);

        // Verify embedded passwords in text strings are replaced
        var sanitizedUrl = sanitized["connectionUrl"]?.ToString();
        Assert.NotNull(sanitizedUrl);
        Assert.Contains("password=[REDACTED_PASSWORD]", sanitizedUrl);
        Assert.DoesNotContain("Secret123", sanitizedUrl);

        // Direct text sanitization check
        var textSanitized = TaskTelemetryAggregator.SanitizeText(
            "Logging in with Bearer eyJhbGciOi... and sk-proj-12345678901234567890 token");
        Assert.DoesNotContain("sk-proj-12345678901234567890", textSanitized);
        Assert.Contains("[REDACTED_AUTH_TOKEN]", textSanitized);
    }

    // =========================================================================
    // 5. Response Diet: Default Slim Is Compact
    // =========================================================================

    [Fact]
    public void ResponseDiet_DefaultSlimIsCompact()
    {
        var response = ResponseDietFormatter.CreateSlim(
            ok: true,
            st: "Verified",
            ev: "42",
            post: new Dictionary<string, object?> { ["value"] = "42", ["enabled"] = true },
            changed: new[] { "input_calc" },
            @new: new Dictionary<string, object?> { ["hwnd"] = "0x1A2B" },
            stateVersion: 15,
            next: NextActionCodes.None
        );

        var json = ResponseDietFormatter.Serialize(response);

        // Verify required canonical keys
        Assert.Contains("\"ok\":true", json);
        Assert.Contains("\"st\":\"Verified\"", json);
        Assert.Contains("\"ev\":\"42\"", json);
        Assert.Contains("\"post\":{", json);
        Assert.Contains("\"changed\":[", json);
        Assert.Contains("\"new\":{", json);
        Assert.Contains("\"stateVersion\":15", json);
        Assert.Contains("\"next\":\"none\"", json);

        // Verify exclusion of verbose bloat in slim mode
        Assert.DoesNotContain("\"bounds\"", json);
        Assert.DoesNotContain("\"session\"", json);
        Assert.DoesNotContain("\"debug\"", json);

        // Verify compact size constraint (must be slim and lightweight)
        Assert.True(json.Length < 300, $"Slim response payload should be compact, was {json.Length} chars: {json}");

        // Test NextAction short enum codes normalization
        Assert.Equal("none", NextActionCodes.Normalize("none"));
        Assert.Equal("observe", NextActionCodes.Normalize("observe"));
        Assert.Equal("observe", NextActionCodes.Normalize("inspect"));
        Assert.Equal("dismiss_modal", NextActionCodes.Normalize("dismiss_modal"));
        Assert.Equal("dismiss_modal", NextActionCodes.Normalize("modal"));
        Assert.Equal("none", NextActionCodes.Normalize("unknown"));
    }

    // =========================================================================
    // 6. Response Diet: Debug Detail Includes Full Bounds
    // =========================================================================

    [Fact]
    public void ResponseDiet_DebugDetailIncludesFullBounds()
    {
        var bounds = new Dictionary<string, int>
        {
            ["x"] = 120,
            ["y"] = 240,
            ["width"] = 800,
            ["height"] = 600
        };

        var session = new Dictionary<string, string>
        {
            ["sessionId"] = "sess_debug_99",
            ["client"] = "InbriskHost"
        };

        var debugDiag = new Dictionary<string, object>
        {
            ["uiaThreadId"] = 42,
            ["comReads"] = 18
        };

        // 1. Debug mode explicitly preserves bounds, session, and debug diagnostics
        var debugResponse = ResponseDietFormatter.CreateDebug(
            ok: true,
            st: "Verified",
            ev: "window_found",
            post: null,
            changed: null,
            @new: null,
            stateVersion: 16,
            next: NextActionCodes.None,
            bounds: bounds,
            session: session,
            debug: debugDiag
        );

        var debugJson = ResponseDietFormatter.Serialize(debugResponse);

        Assert.Contains("\"bounds\":{", debugJson);
        Assert.Contains("\"width\":800", debugJson);
        Assert.Contains("\"session\":{", debugJson);
        Assert.Contains("\"sessionId\":\"sess_debug_99\"", debugJson);
        Assert.Contains("\"debug\":{", debugJson);
        Assert.Contains("\"uiaThreadId\":42", debugJson);

        // 2. Formatting via EnforceDiet strips bounds when debugDetail is false
        var rawDict = new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["st"] = "Verified",
            ["bounds"] = bounds,
            ["session"] = session,
            ["debug"] = debugDiag,
            ["stateVersion"] = 17L,
            ["next"] = "none"
        };

        var slimDict = ResponseDietFormatter.EnforceDiet(rawDict, debugDetail: false);
        Assert.False(slimDict.ContainsKey("bounds"), "Slim mode must strip bounds.");
        Assert.False(slimDict.ContainsKey("session"), "Slim mode must strip session metadata.");
        Assert.False(slimDict.ContainsKey("debug"), "Slim mode must strip debug metadata.");

        var fullDict = ResponseDietFormatter.EnforceDiet(rawDict, debugDetail: true);
        Assert.True(fullDict.ContainsKey("bounds"), "Debug mode must include bounds.");
        Assert.True(fullDict.ContainsKey("session"), "Debug mode must include session.");
        Assert.True(fullDict.ContainsKey("debug"), "Debug mode must include debug.");
    }

    // =========================================================================
    // 7. ErrorReady: Suggests Corrected Arguments
    // =========================================================================

    [Fact]
    public void ErrorReady_SuggestsCorrectedArguments()
    {
        // 1. TargetNotFound with deterministically identifiable match
        var availableButtons = new[] { "Open", "Save", "Close" };
        var originalTargetArgs = new Dictionary<string, object?>
        {
            ["target"] = new Dictionary<string, object?> { ["name"] = "save" },
            ["timeoutMs"] = 1500
        };

        var targetNotFoundError = ErrorReadyArguments.ForTargetNotFound(
            failedTarget: "save",
            availableCandidates: availableButtons,
            originalArgs: originalTargetArgs);

        Assert.Equal(ErrorReadyArguments.TargetNotFoundError, targetNotFoundError.Error);
        Assert.True(targetNotFoundError.RetryReady);
        Assert.NotNull(targetNotFoundError.CorrectedArguments);
        var targetObj = Assert.IsAssignableFrom<IDictionary<string, object?>>(targetNotFoundError.CorrectedArguments["target"]);
        Assert.Equal("Save", targetObj["name"]); // Corrected casing
        Assert.Equal(1500, targetNotFoundError.CorrectedArguments["timeoutMs"]);
        Assert.Equal(NextActionCodes.None, targetNotFoundError.Next);

        // 2. AmbiguousTarget with multiple candidates, defaults to primary disambiguation
        var ambiguousCandidates = new[] { "btn_submit_main", "btn_submit_dialog" };
        var ambiguousError = ErrorReadyArguments.ForAmbiguous(
            targetQuery: "SubmitButton",
            matchingCandidates: ambiguousCandidates);

        Assert.Equal(ErrorReadyArguments.AmbiguousTargetError, ambiguousError.Error);
        Assert.True(ambiguousError.RetryReady);
        Assert.NotNull(ambiguousError.CorrectedArguments);
        Assert.Equal("btn_submit_main", ambiguousError.CorrectedArguments["elementId"]);
        Assert.Equal(0, ambiguousError.CorrectedArguments["index"]);

        // 3. MalformedArguments with deterministic structural correction
        var invalidArgs = new Dictionary<string, object?> { ["coords"] = "150, 250" };
        var correctedArgs = new Dictionary<string, object?> { ["x"] = 150, ["y"] = 250 };

        var malformedError = ErrorReadyArguments.ForMalformed(
            reason: "Coordinates must be separate numeric x and y parameters, not comma-separated string.",
            invalidArgs: invalidArgs,
            correctedArgs: correctedArgs);

        Assert.Equal(ErrorReadyArguments.MalformedArgumentsError, malformedError.Error);
        Assert.True(malformedError.RetryReady);
        Assert.Equal(150, malformedError.CorrectedArguments["x"]);
        Assert.Equal(250, malformedError.CorrectedArguments["y"]);

        // 4. Verify serialization to dictionary format
        var errorDict = targetNotFoundError.ToDictionary();
        Assert.False((bool)errorDict["ok"]!);
        Assert.Equal("Error", errorDict["st"]);
        Assert.True((bool)errorDict["retryReady"]!);
        Assert.NotNull(errorDict["correctedArguments"]);
    }

    // =========================================================================
    // 8. Final Architecture Smoke: All Lanes Integrate Correctly
    // =========================================================================

    [Fact]
    public async Task FinalSmoke_AllLanesIntegrateCorrectly()
    {
        // ---------------------------------------------------------------------
        // Step A: Tool Profiles & Annotation Caching
        // ---------------------------------------------------------------------
        var mockTools = new List<Tool>
        {
            new() { Name = "computer_click", Description = "Click UI element" },
            new() { Name = "computer_windows", Description = "List top-level windows" },
            new() { Name = "browser_browse", Description = "Navigate browser" },
            new() { Name = "custom_debug_tool", Description = "Non-core tool" }
        };

        // Filter and annotate for Core Profile
        var coreTools = McpHost.FilterAndAnnotateTools(mockTools, isCoreProfile: true);
        Assert.Equal(2, coreTools.Count); // computer_click, computer_windows are Core; browser_browse (contextual) and custom_debug_tool are not.
        Assert.DoesNotContain(coreTools, t => t.Name == "browser_browse");
        Assert.DoesNotContain(coreTools, t => t.Name == "custom_debug_tool");

        var clickTool = coreTools.First(t => t.Name == "computer_click");
        Assert.True(clickTool.Annotations?.DestructiveHint);

        var windowsTool = coreTools.First(t => t.Name == "computer_windows");
        Assert.True(windowsTool.Annotations?.ReadOnlyHint);
        Assert.True(windowsTool.Annotations?.IdempotentHint);

        // ---------------------------------------------------------------------
        // Step B: Dynamic Toolset & In-Flight Safety
        // ---------------------------------------------------------------------
        using var toolsetManager = new DynamicToolsetManager();
        Assert.True(toolsetManager.IsToolVisible("computer_launch"));

        // Obtain an in-flight token for an ongoing tool call
        using (var token = toolsetManager.BeginToolCall("computer_launch"))
        {
            Assert.True(token.IsValid);
        }

        // ---------------------------------------------------------------------
        // Step C: Concurrency Lanes (Read Pool & Serial Mutation Lane)
        // ---------------------------------------------------------------------
        using var readPool = new UiaReadPool(maxConcurrency: 2, maxQueueCapacity: 16);
        using var mutationLane = new SerialMutationLane(readPool: readPool);

        // ---------------------------------------------------------------------
        // Step D: World State & Monotonic StateVersion Tracking
        // ---------------------------------------------------------------------
        var stateTracker = new StateVersionTracker(initialVersion: 1);
        Assert.Equal(1, stateTracker.CurrentVersion);

        // ---------------------------------------------------------------------
        // Step E: Telemetry Aggregation Across Lanes
        // ---------------------------------------------------------------------
        var telemetry = new TaskTelemetryAggregator();
        var startTime = DateTimeOffset.UtcNow;

        // 1. Execute Read Lane Operation (Read-only query does not mutate stateVersion)
        var readResult = await readPool.RunAsync(uia =>
        {
            // Simulated read-only UIA hierarchy query
            return "WindowsList: [0x1001, 0x1002]";
        });
        Assert.Equal("WindowsList: [0x1001, 0x1002]", readResult);
        Assert.Equal(1, stateTracker.CurrentVersion); // Unchanged

        telemetry.RecordCall(new CallTelemetryMetrics
        {
            TaskId = "integration-smoke",
            ToolName = "computer_windows",
            ArrivedAt = startTime,
            CompletedAt = startTime.AddMilliseconds(20),
            QueueMs = 2,
            ExecutionMs = 20,
            ReadLaneWaitMs = 1,
            MutationLaneWaitMs = 0,
            ResponseBytes = 250
        });

        // 2. Execute Mutation Lane Operation (Mutates world state and advances stateVersion)
        var mutationResult = await mutationLane.ExecuteAsync("smoke_owner", () =>
        {
            // Simulated click mutation
            stateTracker.RecordMutation("click", elementId: "button_ok");
            return "Clicked button_ok";
        });
        Assert.Equal("Clicked button_ok", mutationResult);
        Assert.Equal(2, stateTracker.CurrentVersion); // Monotonically incremented to 2

        telemetry.RecordCall(new CallTelemetryMetrics
        {
            TaskId = "integration-smoke",
            ToolName = "computer_click",
            ArrivedAt = startTime.AddMilliseconds(100),
            CompletedAt = startTime.AddMilliseconds(140),
            QueueMs = 3,
            ExecutionMs = 40,
            ReadLaneWaitMs = 0,
            MutationLaneWaitMs = 5,
            ResponseBytes = 180
        });

        // ---------------------------------------------------------------------
        // Step F: Response Diet Polish & Serialization
        // ---------------------------------------------------------------------
        var responsePayload = ResponseDietFormatter.CreateSlim(
            ok: true,
            st: "Verified",
            ev: "button_clicked",
            post: new Dictionary<string, object?> { ["status"] = "Submitted" },
            changed: new[] { "button_ok" },
            @new: null,
            stateVersion: stateTracker.CurrentVersion,
            next: NextActionCodes.None
        );

        var slimJson = ResponseDietFormatter.Serialize(responsePayload);

        Assert.Contains("\"ok\":true", slimJson);
        Assert.Contains("\"st\":\"Verified\"", slimJson);
        Assert.Contains("\"stateVersion\":2", slimJson);
        Assert.Contains("\"next\":\"none\"", slimJson);
        Assert.DoesNotContain("\"bounds\"", slimJson);
        Assert.DoesNotContain("\"session\"", slimJson);

        // ---------------------------------------------------------------------
        // Step G: Telemetry Aggregation Summary Verification
        // ---------------------------------------------------------------------
        var summary = telemetry.GetTaskSummary("integration-smoke");
        Assert.Equal(2, summary.TotalToolCalls);
        Assert.Equal(2, summary.SequentialCallCount);
        Assert.Equal(2, summary.EstimatedModelTurns);
        Assert.Equal(1, summary.TotalReadLaneWaitMs);
        Assert.Equal(5, summary.TotalMutationLaneWaitMs);
        Assert.Equal(60, summary.TotalExecutionMs);

        // ---------------------------------------------------------------------
        // Step H: Error-Ready Handling Verification
        // ---------------------------------------------------------------------
        var error = ErrorReadyArguments.ForTargetNotFound(
            failedTarget: "button_cancel",
            availableCandidates: new[] { "button_ok", "button_cancel_all" });
        Assert.True(error.RetryReady);
        Assert.NotNull(error.CorrectedArguments);
    }
}
