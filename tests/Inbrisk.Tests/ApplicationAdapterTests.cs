using System.Diagnostics;
using System.Text.Json;
using Inbrisk.Core;
using Inbrisk.Mcp;
using Inbrisk.Runtime.Adapters;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Inbrisk.Tests;

public class ApplicationAdapterTests
{
    private static InbriskTools CreateToolsWithoutRuntime(ApplicationAdapterRegistry? registry = null)
    {
        var session = (McpSession)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(McpSession));
        typeof(McpSession).GetField("<SessionId>k__BackingField",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(session, "test-adapter-session");
        typeof(McpSession).GetField("<Adapters>k__BackingField",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(session, registry ?? new ApplicationAdapterRegistry());

        var tools = (InbriskTools)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(InbriskTools));
        typeof(InbriskTools).GetField("_s",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(tools, session);
        return tools;
    }

    [Fact]
    public void WindowsMediaAdapter_MetadataAndApplicability()
    {
        var adapter = new WindowsMediaAdapter();
        Assert.Equal("media", adapter.AdapterId);
        Assert.Contains("Media", adapter.DisplayName);
        Assert.Contains("play", adapter.SupportedActions);
        Assert.Contains("pause", adapter.SupportedActions);
        Assert.Contains("next", adapter.SupportedActions);
        Assert.Contains("previous", adapter.SupportedActions);
        Assert.Contains("volume_up", adapter.SupportedActions);
        Assert.Contains("volume_down", adapter.SupportedActions);
        Assert.Contains("mute", adapter.SupportedActions);

        // Applicable to spotify, vlc, etc.
        Assert.True(adapter.IsApplicable("spotify", 100));
        Assert.True(adapter.IsApplicable("Spotify.exe", 100));
        Assert.True(adapter.IsApplicable("vlc", 200));
        Assert.True(adapter.IsApplicable("chrome", 300));
        Assert.False(adapter.IsApplicable("notepad", 400));
        Assert.True(adapter.IsApplicable(null, null)); // Global media key fallback

        // Can handle media actions
        Assert.True(adapter.CanHandle("play", null, null));
        Assert.True(adapter.CanHandle("toggle_playback", null, null));
        Assert.True(adapter.CanHandle("volume_up", null, null));
        Assert.False(adapter.CanHandle("custom_action", null, null));
    }

    [Fact]
    public async Task WindowsMediaAdapter_ExecuteAsync_MediaKeyExecution()
    {
        var adapter = new WindowsMediaAdapter();
        
        // Execute volume_up (virtual key event)
        var result = await adapter.ExecuteAsync("volume_up", null, null, CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal("WindowsMedia.VolumeUp", result.Method);
        Assert.NotNull(result.Data);
        Assert.Equal("volume_up", result.Data!["action"]);

        // Execute invalid action
        var invalidRes = await adapter.ExecuteAsync("unsupported_action", null, null, CancellationToken.None);
        Assert.False(invalidRes.Success);
        Assert.Equal(ErrorCode.Unsupported, invalidRes.Error);
    }

    [Fact]
    public void TestAppAdapter_MetadataAndApplicability()
    {
        var adapter = new TestAppAdapter();
        Assert.Equal("testapp", adapter.AdapterId);
        Assert.Contains("TestApp", adapter.DisplayName);
        Assert.Contains("read_state", adapter.SupportedActions);
        Assert.Contains("save", adapter.SupportedActions);
        Assert.Contains("save_secondary", adapter.SupportedActions);
        Assert.Contains("load_remote", adapter.SupportedActions);
        Assert.Contains("toggle_anim", adapter.SupportedActions);

        // Applicable only to Inbrisk.TestApp
        Assert.True(adapter.IsApplicable("Inbrisk.TestApp", 100));
        Assert.True(adapter.IsApplicable("inbrisk.testapp.exe", 100));
        Assert.False(adapter.IsApplicable("spotify", 200));
        Assert.False(adapter.IsApplicable(null, null));

        Assert.True(adapter.CanHandle("read_state", null, null));
        Assert.True(adapter.CanHandle("save", null, null));
        Assert.False(adapter.CanHandle("play", null, null));
    }

    [Fact]
    public void Registry_DiscoversAndDispatchesAdapters()
    {
        var registry = new ApplicationAdapterRegistry();
        var caps = registry.GetCapabilities();
        Assert.True(caps.Count >= 2);
        Assert.Contains(caps, c => c.AdapterId == "media");
        Assert.Contains(caps, c => c.AdapterId == "testapp");

        // Media matching
        var mediaAdapters = registry.FindApplicable("spotify", 1234);
        Assert.NotEmpty(mediaAdapters);
        Assert.Equal("media", mediaAdapters[0].AdapterId);

        // TestApp matching
        var testAppAdapters = registry.FindApplicable("Inbrisk.TestApp", 5678);
        Assert.NotEmpty(testAppAdapters);
        Assert.Equal("testapp", testAppAdapters[0].AdapterId);

        // Unmatched process
        var noneAdapters = registry.FindApplicable("notepad", 9999);
        Assert.Empty(noneAdapters);
    }

    [Fact]
    public async Task Registry_TryExecuteAsync_RoutesCorrectly()
    {
        var registry = new ApplicationAdapterRegistry();

        // 1. Explicit action known to WindowsMediaAdapter
        var (handled, res) = await registry.TryExecuteAsync(
            action: "mute",
            target: null,
            args: null,
            processName: "spotify",
            hwnd: null,
            preferredAdapterId: null);

        Assert.True(handled);
        Assert.NotNull(res);
        Assert.True(res!.Success);
        Assert.Equal("WindowsMedia.Mute", res.Method);

        // 2. Unknown action -> not handled
        var (handledUnknown, resUnknown) = await registry.TryExecuteAsync(
            action: "fly_to_moon",
            target: null,
            args: null,
            processName: "spotify",
            hwnd: null,
            preferredAdapterId: null);

        Assert.False(handledUnknown);
        Assert.Null(resUnknown);
    }

    [Fact]
    public void StepValidation_AcceptsAdapterAndMediaActions()
    {
        var method = typeof(InbriskTools).GetMethod("ValidateStep",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        // Action: "media" with text "play"
        var step1 = new InbriskTools.RunStep(Action: "media", Text: "play");
        var err1 = (string?)method!.Invoke(null, new object[] { step1 });
        Assert.Null(err1);

        // Action: "adapter" with preferred adapter and args
        var step2 = new InbriskTools.RunStep(
            Action: "adapter",
            Adapter: "testapp",
            Query: "save",
            Args: new Dictionary<string, object?> { ["key"] = "test" });
        var err2 = (string?)method!.Invoke(null, new object[] { step2 });
        Assert.Null(err2);

        // Direct action name: "volume_up"
        var step3 = new InbriskTools.RunStep(Action: "volume_up");
        var err3 = (string?)method!.Invoke(null, new object[] { step3 });
        Assert.Null(err3);
    }

    [Fact]
    public async Task ListAdapters_ReturnsFormattedMcpOutput()
    {
        var tools = CreateToolsWithoutRuntime();
        var result = await tools.ListAdapters();

        Assert.False(result.IsError);
        var text = ((TextContentBlock)result.Content[0]).Text;
        Assert.Contains("Active Application Adapters", text);
        Assert.Contains("[media] Windows Media & Spotify Adapter", text);
        Assert.Contains("[testapp] Inbrisk TestApp Specialist Adapter", text);
    }

    [Fact]
    public async Task AdapterExecute_ExecutesDirectMediaAction()
    {
        var tools = CreateToolsWithoutRuntime();
        var result = await tools.AdapterExecute(
            action: "mute",
            adapter: "media",
            target: null,
            args: null);

        Assert.False(result.IsError);
        var text = ((TextContentBlock)result.Content[0]).Text;
        using var doc = JsonDocument.Parse(text);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("WindowsMedia.Mute", doc.RootElement.GetProperty("method").GetString());
    }
}
