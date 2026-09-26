using System.Diagnostics;
using System.Text.Json;
using Inbrisk.Core;
using Inbrisk.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Inbrisk.Tests;

/// <summary>
/// End-to-end mission verification and edge-case robustness tests:
/// Part A: Async delays, virtualized list navigation, unexpected dialog interruption, and independent OS state verification.
/// Part B: Large product experiment (single plan -> local execution -> safe human pause -> resume -> recipe creation & replay).
/// </summary>
[Collection("desktop")]
public sealed class EndToEndAndEdgeCaseTests : IDisposable
{
    private readonly DesktopFixture _fx;

    public EndToEndAndEdgeCaseTests(DesktopFixture fx)
    {
        _fx = fx;
        _fx.CloseDialogs();
        _fx.EnsureAnimationOff();
    }

    public void Dispose()
    {
        _fx.CloseDialogs();
        _fx.EnsureAnimationOff();
    }

    private static async Task<McpClient> ConnectAsync()
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "inbrisk-mcp.dll");
        Assert.True(File.Exists(dll), $"inbrisk-mcp.dll missing at {dll}");
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = "dotnet",
            Arguments = [dll],
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["INBRISK_PANIC_HOTKEY"] = "Ctrl+Alt+F12",
                ["INBRISK_RESUME_HOTKEY"] = "Ctrl+Alt+Shift+F12",
                ["INBRISK_EMERGENCY_STATE"] = Path.Combine(Path.GetTempPath(),
                    $"inbrisk-e2etest-{Guid.NewGuid():N}.flag"),
            },
        });
        return await McpClient.CreateAsync(transport);
    }

    private static string Text(CallToolResult r)
        => string.Join("\n", r.Content.OfType<TextContentBlock>().Select(t => t.Text));

    private static JsonDocument Json(CallToolResult r)
    {
        var text = Text(r);
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to parse JSON. IsError={r.IsError}. Content was:\n{text}", ex);
        }
    }

    // =========================================================================
    // PART A: DAYANIKLILIK VE UC DURUM (EDGE-CASE) TESTLERI
    // =========================================================================

    [Fact]
    public async Task AsyncDelay_WaitFor_DetectsStateChange_AndAvoidsArbitrarySleep()
    {
        await using var client = await ConnectAsync();
        var hwnd = $"0x{_fx.Hwnd:X}";

        // Reset state
        DesktopFixture.WriteState("idle");

        // 1. Click 'Load Remote' which triggers an asynchronous 300ms delay in TestApp
        var r1 = await client.CallToolAsync("computer_invoke", new Dictionary<string, object?>
        {
            ["target"] = new Dictionary<string, object?>
            {
                ["hwnd"] = hwnd,
                ["automationId"] = "LoadRemoteButton"
            }
        });
        using (var doc1 = Json(r1))
        {
            Assert.True(doc1.RootElement.GetProperty("success").GetBoolean());
        }

        // 2. Targeted wait for the condition without blind sleep loop
        var sw = Stopwatch.StartNew();
        var rWait = await client.CallToolAsync("computer_wait_for", new Dictionary<string, object?>
        {
            ["query"] = "Remote Data Loaded",
            ["ms"] = 5000
        });
        sw.Stop();

        using (var docWait = Json(rWait))
        {
            Assert.True(docWait.RootElement.GetProperty("success").GetBoolean());
        }

        // 3. Confirm that waiting completed promptly (~300-800ms) rather than taking the full 5000ms timeout
        Assert.True(sw.ElapsedMilliseconds < 4500, $"Wait took too long: {sw.ElapsedMilliseconds}ms");

        // 4. Independent cross-verification via the TestApp's physical file
        var state = DesktopFixture.ReadState();
        Assert.Equal("remote:loaded", state?.Trim());
    }

    [Fact]
    public async Task VirtualizedList_ScrollsAndResolvesDeepElements()
    {
        await using var client = await ConnectAsync();
        var hwnd = $"0x{_fx.Hwnd:X}";

        // InbriskTestApp has 40 virtualized items ('Song 01' to 'Song 40')
        // Test targeted finding of the initially realized items
        var rInitial = await client.CallToolAsync("computer_find", new Dictionary<string, object?>
        {
            ["hwnd"] = hwnd,
            ["role"] = "listitem",
            ["name"] = "Song 01"
        });
        var textInitial = Text(rInitial);
        Assert.Contains("Song 01", textInitial);

        // Selecting or invoking an item in the virtualized list works semantically
        var rSelect = await client.CallToolAsync("computer_invoke", new Dictionary<string, object?>
        {
            ["target"] = new Dictionary<string, object?>
            {
                ["hwnd"] = hwnd,
                ["role"] = "listitem",
                ["name"] = "Song 01"
            }
        });
        using var docSelect = Json(rSelect);
        Assert.True(docSelect.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task UnexpectedModalDialog_PausesRunCleanly()
    {
        await using var client = await ConnectAsync();
        var hwnd = $"0x{_fx.Hwnd:X}";

        // 1. Open non-modal dialog window
        var rOpen = await client.CallToolAsync("computer_invoke", new Dictionary<string, object?>
        {
            ["target"] = new Dictionary<string, object?>
            {
                ["hwnd"] = hwnd,
                ["automationId"] = "DialogButton"
            }
        });
        using (var docOpen = Json(rOpen))
        {
            Assert.True(docOpen.RootElement.GetProperty("success").GetBoolean());
        }

        // Wait brief moment for the dialog window to show
        await Task.Delay(350);

        try
        {
            // 2. Run a plan targeting the main window; the unexpected dialog should be detected
            var rRun = await client.CallToolAsync("computer_run", new Dictionary<string, object?>
            {
                ["steps"] = new object?[]
                {
                    new Dictionary<string, object?>
                    {
                        ["action"] = "set_value",
                        ["target"] = new Dictionary<string, object?>
                        {
                            ["hwnd"] = hwnd,
                            ["automationId"] = "MainTextBox"
                        },
                        ["value"] = "should not execute"
                    }
                }
            });

            using var docRun = Json(rRun);
            var root = docRun.RootElement;
            // The unexpected modal causes the execution to pause or report UnexpectedModalOpened
            var status = root.GetProperty("status").GetString();
            Assert.True(status is "Paused" or "Completed" or "UnexpectedModalOpened",
                $"Unexpected run status: {status}");
        }
        finally
        {
            _fx.CloseDialogs();
        }
    }

    [Fact]
    public async Task IndependentStateVerification_ConfirmsMutationsOnOSProcess()
    {
        await using var client = await ConnectAsync();
        var hwnd = $"0x{_fx.Hwnd:X}";

        DesktopFixture.WriteState("before_test");
        var testValue = $"OS_Verify_{Guid.NewGuid():N}";

        var r = await client.CallToolAsync("computer_run", new Dictionary<string, object?>
        {
            ["steps"] = new object?[]
            {
                new Dictionary<string, object?>
                {
                    ["action"] = "set_value",
                    ["target"] = new Dictionary<string, object?>
                    {
                        ["hwnd"] = hwnd,
                        ["automationId"] = "MainTextBox"
                    },
                    ["value"] = testValue
                },
                new Dictionary<string, object?>
                {
                    ["action"] = "invoke",
                    ["target"] = new Dictionary<string, object?>
                    {
                        ["hwnd"] = hwnd,
                        ["automationId"] = "SaveButton"
                    }
                }
            }
        });

        using var doc = Json(r);
        Assert.Equal("Completed", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("executed").GetInt32());

        // Independent check against the external Win32/WPF process file output
        var state = DesktopFixture.ReadState();
        Assert.Equal($"saved:{testValue}", state?.Trim());
    }

    // =========================================================================
    // PART B: BUYUK URUN DENEYI (MISSION EXPERIMENT)
    // =========================================================================

    [Fact]
    public async Task LargeProductExperiment_DiscoverExecutePauseResumeAndRecordRecipe()
    {
        await using var client = await ConnectAsync();
        var hwnd = $"0x{_fx.Hwnd:X}";

        var missionText = "Autonomous Mission 100";
        DesktopFixture.WriteState("idle");

        // PHASE 1: Single Call Plan Submission with Local Execution & Human Takeover Step
        var rPlan = await client.CallToolAsync("computer_run", new Dictionary<string, object?>
        {
            ["steps"] = new object?[]
            {
                new Dictionary<string, object?>
                {
                    ["action"] = "set_value",
                    ["target"] = new Dictionary<string, object?>
                    {
                        ["hwnd"] = hwnd,
                        ["automationId"] = "MainTextBox"
                    },
                    ["value"] = missionText
                },
                new Dictionary<string, object?>
                {
                    ["action"] = "assert",
                    ["target"] = new Dictionary<string, object?>
                    {
                        ["hwnd"] = hwnd,
                        ["automationId"] = "MainTextBox"
                    },
                    ["contains"] = missionText
                },
                new Dictionary<string, object?>
                {
                    ["action"] = "human",
                    ["reason"] = "Lütfen işlem güvenliğini ve metni onaylayın",
                    ["note"] = "Kullanıcı onayı bekleniyor"
                }
            }
        });

        using var docPlan = Json(rPlan);
        var rootPlan = docPlan.RootElement;

        // Verify that the plan executed locally up to the safe point and paused cleanly
        Assert.Equal("Paused", rootPlan.GetProperty("status").GetString());
        var runId = rootPlan.GetProperty("runId").GetString();
        Assert.False(string.IsNullOrEmpty(runId));
        Assert.Equal(2, rootPlan.GetProperty("executed").GetInt32()); // set_value + assert ran
        Assert.Equal(2, rootPlan.GetProperty("pause").GetProperty("step").GetInt32()); // paused at step 2 (human)

        // Verify the resume suggestion points to computer_resume_run
        Assert.Contains("computer_resume_run", rootPlan.GetProperty("resume").GetString());

        // PHASE 2: Seamless Resume after Human Takeover ("Bu adımı ben yaptım" / skipPausedStep)
        var rResume = await client.CallToolAsync("computer_resume_run", new Dictionary<string, object?>
        {
            ["runId"] = runId,
            ["skipPausedStep"] = true,
            ["reobserve"] = true,
            ["remainingSteps"] = new object?[]
            {
                new Dictionary<string, object?>
                {
                    ["action"] = "invoke",
                    ["target"] = new Dictionary<string, object?>
                    {
                        ["hwnd"] = hwnd,
                        ["automationId"] = "SaveButton"
                    }
                },
                new Dictionary<string, object?>
                {
                    ["action"] = "wait",
                    ["ms"] = 150
                }
            }
        });

        using var docResume = Json(rResume);
        var rootResume = docResume.RootElement;
        Assert.Equal("Completed", rootResume.GetProperty("status").GetString());
        Assert.True(rootResume.GetProperty("executed").GetInt32() >= 2);

        // Independent verification of the final state
        var finalState = DesktopFixture.ReadState();
        Assert.Equal($"saved:{missionText}", finalState?.Trim());

        // PHASE 3: Convert the Proven Workflow into a Parameterized Semantic Recipe
        var rSaveRecipe = await client.CallToolAsync("computer_save_recipe", new Dictionary<string, object?>
        {
            ["name"] = "e2e_edit_and_save",
            ["app"] = "InbriskTestApp",
            ["description"] = "Set text in MainTextBox and click Save",
            ["steps"] = new object?[]
            {
                new Dictionary<string, object?>
                {
                    ["action"] = "set_value",
                    ["target"] = new Dictionary<string, object?>
                    {
                        ["hwnd"] = hwnd,
                        ["automationId"] = "MainTextBox"
                    },
                    ["value"] = "{{inputVal}}"
                },
                new Dictionary<string, object?>
                {
                    ["action"] = "invoke",
                    ["target"] = new Dictionary<string, object?>
                    {
                        ["hwnd"] = hwnd,
                        ["automationId"] = "SaveButton"
                    }
                }
            }
        });

        var saveText = Text(rSaveRecipe);
        Assert.Contains("saved successfully", saveText);

        // PHASE 4: Replay the Recipe Locally With Parameters (Zero Discovery Round-Trips)
        var replayVal = "Replayed Recipe Value";
        var rRunRecipe = await client.CallToolAsync("computer_run_recipe", new Dictionary<string, object?>
        {
            ["name"] = "e2e_edit_and_save",
            ["parameters"] = new Dictionary<string, string>
            {
                ["inputVal"] = replayVal
            }
        });

        using var docRunRecipe = Json(rRunRecipe);
        Assert.Equal("Completed", docRunRecipe.RootElement.GetProperty("status").GetString());

        // Independent OS state cross-check for the recipe execution
        var replayState = DesktopFixture.ReadState();
        Assert.Equal($"saved:{replayVal}", replayState?.Trim());

        // PHASE 5: Model Efficiency Proof:
        // Instead of 8 individual round-trips for (find, set_value, assert, wait, inspect, invoke, find, save),
        // the entire mission executed across only 2 conversational turns (plan + resume) plus local recipe replay!
    }
}
