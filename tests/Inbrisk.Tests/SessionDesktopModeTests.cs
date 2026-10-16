using System.Text.Json;
using Inbrisk.Core;
using Inbrisk.Mcp;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace Inbrisk.Tests;

/// <summary>
/// The session targets ONE desktop, chosen once. Before this, every ghost-
/// capable tool carried its own `ghost` flag with an identical 154-character
/// description — nine copies of the same decision, and no way to say "this
/// whole session is on the background desktop" in one call.
///
/// The interesting behaviour is not the flag, it is the invalidation: a
/// uia_* element id, a frameId and a scope hwnd minted on one desktop name
/// nothing on the other. Switching without dropping them would silently
/// resolve against a window that isn't there.
/// </summary>
public sealed class SessionDesktopModeTests
{
    private static string TextOf(CallToolResult r) => string.Join("\n",
        r.Content.OfType<TextContentBlock>().Select(t => t.Text));

    private static JsonElement JsonOf(CallToolResult r)
    {
        using var doc = JsonDocument.Parse(TextOf(r));
        return doc.RootElement.Clone();
    }

    // ---------- name parsing ----------

    [Theory]
    [InlineData("ghost", SessionDesktop.Ghost)]
    [InlineData("GHOST", SessionDesktop.Ghost)]
    [InlineData(" background ", SessionDesktop.Ghost)]
    [InlineData("isolated", SessionDesktop.Ghost)]
    [InlineData("interactive", SessionDesktop.Interactive)]
    [InlineData("local", SessionDesktop.Interactive)]
    [InlineData("live", SessionDesktop.Interactive)]
    public void TryParse_ResolvesDesktopNames(string text, SessionDesktop expected)
    {
        Assert.True(SessionDesktops.TryParse(text, out var got));
        Assert.Equal(expected, got);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("remote")]
    [InlineData("true")]
    public void TryParse_RejectsAnythingElse_WithoutFallingBack(string? text)
    {
        // Silently defaulting to interactive would be the worst outcome: the
        // caller asked for a desktop and got the user's live one instead.
        Assert.False(SessionDesktops.TryParse(text, out _));
    }

    // ---------- session state ----------

    [Fact]
    public void NewSession_IsInteractive()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        Assert.Equal(SessionDesktop.Interactive, session.Desktop);
        Assert.False(session.OnGhostDesktop);
    }

    [Fact]
    public void Session_CanStartOnTheGhostDesktop()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false,
            initialDesktop: SessionDesktop.Ghost);
        Assert.True(session.OnGhostDesktop);
        Assert.True(session.ResolveGhost(perCall: null));
    }

    [Fact]
    public void PerCallOverride_Wins_ButOmittingIt_FollowsTheSession()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);

        // session says interactive
        Assert.False(session.ResolveGhost(perCall: null));
        Assert.True(session.ResolveGhost(perCall: true));
        Assert.False(session.ResolveGhost(perCall: false));

        session.SetDesktop(SessionDesktop.Ghost);

        // session says ghost
        Assert.True(session.ResolveGhost(perCall: null));
        Assert.False(session.ResolveGhost(perCall: false));
        Assert.True(session.ResolveGhost(perCall: true));
    }

    // ---------- invalidation on switch ----------

    [Fact]
    public void SwitchingDesktops_DropsEveryDesktopScopedHandle()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        session.ValidObsIds.Add(4242);
        session.Runs["run-1"] = new RunState { RunId = "run-1" };
        session.PrevOutcome = new StepOutcome(OutcomeKind.Verified, true, "click", null, 5);
        session.LastLaunchedHwnd = 0xABCD;
        session.NegativeFindCache["nothing"] = (new TargetDiagnosis("TargetNotFound", "no"),
            DateTimeOffset.UtcNow, [], [], 1);

        Assert.True(session.SetDesktop(SessionDesktop.Ghost));

        // A uia_* id / frameId from the interactive desktop names nothing on
        // the ghost desktop — resolving against one is worse than not having it.
        Assert.Empty(session.ValidObsIds);
        Assert.Empty(session.Frames);
        Assert.Empty(session.FrameObservations);
        // A paused run holds element bindings from the old desktop.
        Assert.Empty(session.Runs);
        // The delta baseline and post-action context are from the old desktop.
        Assert.Null(session.PrevOutcome);
        Assert.Equal(0, session.LastLaunchedHwnd);
        Assert.Empty(session.NegativeFindCache);
    }

    [Fact]
    public void SettingTheModeTheSessionAlreadyHas_ChangesNothing()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        session.ValidObsIds.Add(7);

        Assert.False(session.SetDesktop(SessionDesktop.Interactive));

        // A redundant switch must not cost a re-discovery round trip.
        Assert.Contains(7, session.ValidObsIds);
    }

    // ---------- the tool ----------

    [Fact]
    public async Task SessionTool_ReadsTheCurrentMode_WhenModeIsOmitted()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        var json = JsonOf(await tools.Session());

        Assert.Equal("interactive", json.GetProperty("desktop").GetString());
        Assert.False(json.GetProperty("changed").GetBoolean());
        Assert.Equal(session.SessionId, json.GetProperty("sessionId").GetString());
    }

    [Fact]
    public async Task SessionTool_SwitchesTheSession_Once()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        var switched = JsonOf(await tools.Session("ghost"));
        Assert.Equal("ghost", switched.GetProperty("desktop").GetString());
        Assert.True(switched.GetProperty("changed").GetBoolean());
        Assert.Equal("interactive", switched.GetProperty("previous").GetString());
        Assert.True(session.OnGhostDesktop);
        // The isolated desktop is not started by the switch, only reported.
        Assert.True(switched.TryGetProperty("ghostSession", out var gs));
        Assert.False(gs.GetProperty("active").GetBoolean());

        var again = JsonOf(await tools.Session("ghost"));
        Assert.Equal("ghost", again.GetProperty("desktop").GetString());
        Assert.False(again.GetProperty("changed").GetBoolean());

        var back = JsonOf(await tools.Session("interactive"));
        Assert.Equal("interactive", back.GetProperty("desktop").GetString());
        Assert.True(back.GetProperty("changed").GetBoolean());
        Assert.Equal("ghost", back.GetProperty("previous").GetString());
        // back on the interactive desktop — no ghost reachability block
        Assert.False(back.TryGetProperty("ghostSession", out _));
    }

    [Fact]
    public async Task SessionTool_RejectsAnUnknownDesktop()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        var result = await tools.Session("remote-desktop");

        Assert.True(result.IsError);
        Assert.Contains("Malformed", TextOf(result));
        Assert.Equal(SessionDesktop.Interactive, session.Desktop);
    }

    // ---------- the contract surface ----------

    [Theory]
    [InlineData(null, "interactive")]
    [InlineData("ghost", "ghost")]
    public void Capabilities_ReportTheSessionDesktop(string? mode, string expected)
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);
        if (mode != null) session.SetDesktop(SessionDesktop.Ghost);

        foreach (var detail in new[] { "slim", "full" })
        {
            var json = JsonOf(tools.Capabilities(detail));
            Assert.Equal(expected, json.GetProperty("sessionDesktop").GetString());
        }
    }

    [Fact]
    public void ComputerSession_IsInTheCoreToolProfile()
    {
        // A core-profile session that cannot move itself onto the isolated
        // desktop would be stuck on the user's live one.
        Assert.Contains("computer_session", McpHost.CoreTools);
    }

    [Fact]
    public void GhostFlags_AreOptionalOverrides_OnEveryGhostCapableTool()
    {
        foreach (var name in GhostCapableMethods)
        {
            var param = typeof(InbriskTools)
                .GetMethod(name)!
                .GetParameters()
                .Single(p => p.Name == "ghost");

            // A required or non-nullable bool would force every caller to
            // restate the session's decision on every single call.
            Assert.True(param.HasDefaultValue, $"{name}.ghost must be optional");
            Assert.Equal(typeof(bool?), param.ParameterType);
            Assert.Null(param.DefaultValue);
        }
    }

    [Fact]
    public void GhostCapableToolSet_IsExactlyWhatComputerSessionAdvertises()
    {
        // computer_session tells the model which tools follow the session
        // mode. If a tool gains ghost routing without the description
        // following, the description under-promises silently; if a tool
        // loses it, the model will route real input to the wrong desktop.
        var advertised = typeof(InbriskTools)
            .GetMethod(nameof(InbriskTools.Session))!
            .GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false)
            .Cast<System.ComponentModel.DescriptionAttribute>()
            .Select(a => a.Description)
            .Single();

        foreach (var tool in GhostCapableMcpTools)
        {
            var isDeclared = typeof(InbriskTools)
                .GetMethods()
                .Any(m => m.GetCustomAttributes(typeof(McpServerToolAttribute), false)
                    .Cast<McpServerToolAttribute>()
                    .Any(a => a.Name == tool)
                    && m.GetParameters().Any(p => p.Name == "ghost"));

            Assert.True(isDeclared, $"{tool} is advertised by computer_session but has no ghost route");
            Assert.Contains(tool, advertised);
        }

        // nothing advertises a tool that does not exist
        foreach (var declared in typeof(InbriskTools)
            .GetMethods()
            .Where(m => m.GetParameters().Any(p => p.Name == "ghost"))
            .SelectMany(m => m.GetCustomAttributes(typeof(McpServerToolAttribute), false)
                .Cast<McpServerToolAttribute>())
            .Select(a => a.Name))
        {
            Assert.Contains(declared, advertised);
        }
    }

    private static readonly string[] GhostCapableMethods =
    {
        nameof(InbriskTools.Observe), nameof(InbriskTools.Windows), nameof(InbriskTools.Launch),
        nameof(InbriskTools.Inspect), nameof(InbriskTools.Click), nameof(InbriskTools.Type),
        nameof(InbriskTools.ComputerDo), nameof(InbriskTools.Batch), nameof(InbriskTools.Screenshot),
    };

    private static readonly string[] GhostCapableMcpTools =
    {
        "computer_observe", "computer_windows", "computer_launch", "computer_inspect",
        "computer_click", "computer_type", "computer_do", "computer_batch", "computer_screenshot",
    };
}