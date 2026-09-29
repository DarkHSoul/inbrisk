using System.Diagnostics;
using System.Text.Json;
using Inbrisk.Core;
using Inbrisk.Mcp;
using Inbrisk.Platform.Windows.Hud;
using Inbrisk.Runtime;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Inbrisk.Tests;

public class HumanTakeoverAndResumeTests
{
    private static InbriskTools CreateToolsForTakeover(out McpSession session, out RunState state)
    {
        session = (McpSession)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(McpSession));
        typeof(McpSession).GetField("<SessionId>k__BackingField",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(session, "test-takeover-session");
        typeof(McpSession).GetField("<Arbiter>k__BackingField",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(session, DesktopArbiter.Shared);
        typeof(McpSession).GetField("<SessionCts>k__BackingField",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(session, new CancellationTokenSource());

        var control = (EmergencyControl)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(EmergencyControl));
        typeof(EmergencyControl).GetField("_state",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(control, ComputerControlState.Active);
        typeof(EmergencyControl).GetField("_epoch",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(control, new CancellationTokenSource());
        typeof(EmergencyControl).GetField("_peerAuthority",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(control, true);
        typeof(EmergencyControl).GetField("_gate",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(control, new object());
        typeof(McpSession).GetField("<Control>k__BackingField",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(session, control);

        var hud = new ActivityHudService(enabled: false, animationsEnabled: false, alwaysVisible: true);
        var inbriskObj = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Inbrisk.Sdk.InbriskRuntime));
        typeof(Inbrisk.Sdk.InbriskRuntime).GetField("_hud",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(inbriskObj, hud);
        typeof(Inbrisk.Sdk.InbriskRuntime).GetField("_windows",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(inbriskObj, new Inbrisk.Platform.Windows.Topology.WindowService());
        typeof(Inbrisk.Sdk.InbriskRuntime).GetField("_activity",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(inbriskObj, new ComputerControlActivityService());
        typeof(McpSession).GetField("<Rt>k__BackingField",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(session, inbriskObj);

        var runs = new Dictionary<string, RunState>();
        state = new RunState
        {
            RunId = "run_test_takeover_1",
            PausedStepIndex = 1,
            PlannedSteps = new InbriskTools.RunStep[]
            {
                new(Action: "wait", Ms: 10),
                new(Action: "human", Note: "Lütfen CAPTCHA çözün"),
                new(Action: "wait", Ms: 10)
            }
        };
        runs[state.RunId] = state;
        typeof(McpSession).GetField("<Runs>k__BackingField",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(session, runs);

        var validObs = new HashSet<long> { 101, 102 };
        typeof(McpSession).GetField("<ValidObsIds>k__BackingField",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(session, validObs);
        var frames = new Dictionary<long, FrameRef>
        {
            [1] = (FrameRef)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(FrameRef))
        };
        typeof(McpSession).GetField("<Frames>k__BackingField",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(session, frames);
        typeof(McpSession).GetField("<FrameObservations>k__BackingField",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(session, new Dictionary<long, long>());
        typeof(McpSession).GetField("_observationHistory",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(session, new Dictionary<long, AgentObservation>());
        typeof(McpSession).GetField("_recentRefs",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(session, new List<string>());

        var tools = (InbriskTools)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(InbriskTools));
        typeof(InbriskTools).GetField("_s",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(tools, session);
        return tools;
    }

    [Fact]
    public void ActivityHudService_SetHumanTakeover_SetsStandbyAndFormatsTwoLines()
    {
        using var hud = new ActivityHudService(enabled: false, animationsEnabled: false, alwaysVisible: true);
        hud.SetHumanTakeover("Kullanıcı müdahalesi bekleniyor", "Tıkla 'Devam Et'");

        Assert.Equal(HudState.Standby, hud.State);
        Assert.StartsWith("⏸", hud.CurrentText);
        Assert.Contains("Kullanıcı müdahalesi bekleniyor", hud.CurrentText);
        Assert.Equal("Sıradaki: Tıkla 'Devam Et'", hud.PausedSubtitle);

        // Transition back to working clears the subtitle
        hud.SetActivity("Gözlem yapılıyor…");
        Assert.Equal(HudState.Working, hud.State);
        Assert.Null(hud.PausedSubtitle);
    }

    [Fact]
    public void ActivityHudService_SetEmergency_False_UnconditionallyClearsEmergencyText()
    {
        // This test directly verifies the resolution of the bug where pill bar was stuck on "■ Inbrisk durduruldu"
        using var hud = new ActivityHudService(enabled: false, animationsEnabled: false, alwaysVisible: true);
        
        hud.SetEmergency(true);
        Assert.Equal(HudState.Emergency, hud.State);
        Assert.Equal("■ Inbrisk durduruldu", hud.CurrentText);

        hud.SetEmergency(false);
        Assert.Equal(HudState.Standby, hud.State);
        Assert.Equal("Inbrisk Hazır", hud.CurrentText);
        Assert.Null(hud.PausedSubtitle);
    }

    [Fact]
    public void ValidateStep_SupportsHumanAction_AndReasonField()
    {
        var step1 = new InbriskTools.RunStep(Action: "human", Note: "Solve captcha");
        var step2 = new InbriskTools.RunStep(Action: "human_takeover", Reason: "Login with credentials");
        var step3 = new InbriskTools.RunStep(Action: "pause_for_human", Reason: "Verify dialog");
        var badStep = new InbriskTools.RunStep(Action: "human", Text: "invalid_field");

        Assert.Null(InbriskTools.ValidateStep(step1));
        Assert.Null(InbriskTools.ValidateStep(step2));
        Assert.Null(InbriskTools.ValidateStep(step3));

        var err = InbriskTools.ValidateStep(badStep);
        Assert.NotNull(err);
        Assert.Contains("text", err);
    }

    [Fact]
    public void DescribeStep_ProducesHumanReadableSummaries()
    {
        var mi = typeof(InbriskTools).GetMethod("DescribeStep",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(mi);

        var clickStep = new InbriskTools.RunStep(Action: "click", Target: new InbriskTools.TargetSpec(Name: "Submit Button"));
        var typeStep = new InbriskTools.RunStep(Action: "type", Text: "Search query");
        var hotkeyStep = new InbriskTools.RunStep(Action: "hotkey", Keys: "ctrl+s");
        var humanStep = new InbriskTools.RunStep(Action: "human", Reason: "Lütfen onaylayın");

        var descClick = (string?)mi!.Invoke(null, [clickStep]);
        var descType = (string?)mi.Invoke(null, [typeStep]);
        var descHotkey = (string?)mi.Invoke(null, [hotkeyStep]);
        var descHuman = (string?)mi.Invoke(null, [humanStep]);

        Assert.Equal("click \"Submit Button\"", descClick);
        Assert.Equal("type \"Search query\"", descType);
        Assert.Equal("hotkey ctrl+s", descHotkey);
        Assert.Equal("human: Lütfen onaylayın", descHuman);
    }

    [Fact]
    public void PauseRun_UpdatesRunState_AndHUD()
    {
        var tools = CreateToolsForTakeover(out var session, out var state);
        var result = tools.PauseRun("run_test_takeover_1", "Test human pause");

        Assert.False(result.IsError);
        Assert.NotNull(result.Content);
        var jsonText = ((TextContentBlock)result.Content[0]).Text;
        using var doc = JsonDocument.Parse(jsonText);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal("PausedForHuman", root.GetProperty("status").GetString());
        Assert.Equal("Test human pause", root.GetProperty("reason").GetString());
        Assert.True(state.IsPausedForHuman);
        Assert.True(state.SafePointPauseRequested);
        Assert.Equal("Test human pause", state.PauseReason);

        var hud = session.Rt.Hud;
        Assert.StartsWith("⏸", hud.CurrentText);
        Assert.NotNull(hud.PausedSubtitle);
    }

    [Fact]
    public async Task ResumeRun_InvalidatesStaleHandles_AndResetsPauseFlags()
    {
        var tools = CreateToolsForTakeover(out var session, out var state);
        state.IsPausedForHuman = true;
        state.SafePointPauseRequested = true;
        state.PauseStatus = "PausedForHuman";
        state.PauseReason = "Waiting for human";
        state.PausedStepIndex = 1;
        state.PlannedSteps = new InbriskTools.RunStep[]
        {
            new(Action: "wait", Ms: 10),
            new(Action: "human", Note: "Lütfen CAPTCHA çözün")
        };

        // Validate that before resume we had handles
        Assert.NotEmpty(session.ValidObsIds);
        Assert.NotEmpty(session.Frames);

        // Resume with skipPausedStep = true (step 1 skipped -> reached end -> Completed)
        var result = await tools.ResumeRun(state.RunId, skipPausedStep: true, reobserve: false);

        // 1. Stale handles invalidated
        Assert.Empty(session.ValidObsIds);
        Assert.Empty(session.Frames);

        // 2. Pause flags cleared
        Assert.False(state.IsPausedForHuman);
        Assert.False(state.SafePointPauseRequested);
        Assert.Null(state.PauseStatus);
        Assert.Null(state.PauseReason);
    }

    [Fact]
    public void McpSession_InvalidateStaleHandles_WipesCaches()
    {
        var session = (McpSession)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(McpSession));
        typeof(McpSession).GetField("<ValidObsIds>k__BackingField",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(session, new HashSet<long> { 1, 2, 3 });
        var dummyFrame = (FrameRef)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(FrameRef));
        typeof(McpSession).GetField("<Frames>k__BackingField",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(session, new Dictionary<long, FrameRef> { [1] = dummyFrame, [2] = dummyFrame });
        typeof(McpSession).GetField("<FrameObservations>k__BackingField",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(session, new Dictionary<long, long> { [1] = 100 });
        typeof(McpSession).GetField("_observationHistory",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(session, new Dictionary<long, AgentObservation>());
        typeof(McpSession).GetField("_recentRefs",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(session, new List<string> { "el_1" });

        session.InvalidateStaleHandles();

        Assert.Empty(session.ValidObsIds);
        Assert.Empty(session.Frames);
    }
}
