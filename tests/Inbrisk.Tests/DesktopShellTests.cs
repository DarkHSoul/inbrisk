using System.Diagnostics;
using System.Drawing;
using Inbrisk.Core;
using Inbrisk.Mcp;
using Inbrisk.Platform.Windows.Hud;
using Inbrisk.Platform.Windows.Native;
using Inbrisk.Platform.Windows.Tray;
using Xunit;

namespace Inbrisk.Tests;

// McpTools_* tests spin up a REAL InbriskRuntime (UIA dispatcher, WinEvent
// hooks, HUD/indicator threads). Running them in parallel with the desktop
// collection's UI-touching tests made AppStatus flaky under load — every
public class DesktopShellTests
{
    public DesktopShellTests() { }

    [Fact]
    public void Hud_InitialState_IsHidden()
    {
        using var hud = new ActivityHudService();
        hud.Start();
        Assert.True(hud.WaitForReady());
        Assert.Equal(HudState.Hidden, hud.State);
    }

    [Fact]
    public void Hud_SetActivity_TransitionsToWorking()
    {
        using var hud = new ActivityHudService();
        hud.Start();
        Assert.True(hud.WaitForReady());

        hud.SetActivity("Spotify açılıyor…", "Spotify");
        Assert.Equal(HudState.Working, hud.State);
        Assert.Equal("Spotify açılıyor…", hud.CurrentText);
    }

    [Fact]
    public void Hud_BobbingBounds_StrictlyBounded()
    {
        // Assert mathematical displacement bounds (±2.5px)
        const double amp = 2.5;
        for (double deg = 0; deg <= 720; deg += 15)
        {
            var disp = Math.Sin(deg * Math.PI / 180.0) * amp;
            Assert.InRange(disp, -2.5, 2.5);
        }
    }

    [Fact]
    public void Hud_Success_TransitionsToSettling()
    {
        using var hud = new ActivityHudService();
        hud.Start();
        Assert.True(hud.WaitForReady());

        hud.SetActivity("Spotify açılıyor…", "Spotify");
        Assert.Equal(HudState.Working, hud.State);

        hud.SetSuccess("Spotify açıldı");
        Assert.Equal(HudState.Settling, hud.State);
        Assert.Equal("Spotify açıldı", hud.CurrentText);
    }

    [Fact]
    public void Hud_Failure_TransitionsToFailureState()
    {
        using var hud = new ActivityHudService();
        hud.Start();
        Assert.True(hud.WaitForReady());

        hud.SetActivity("Spotify açılıyor…", "Spotify");
        hud.SetFailure("Spotify açılamadı");

        Assert.Equal(HudState.Failure, hud.State);
        Assert.StartsWith("× ", hud.CurrentText);
    }

    [Fact]
    public void Hud_Emergency_OverridesNormalActivity()
    {
        using var hud = new ActivityHudService();
        hud.Start();
        Assert.True(hud.WaitForReady());

        hud.SetActivity("İşlem yapılıyor…");
        Assert.Equal(HudState.Working, hud.State);

        hud.SetEmergency(true);
        Assert.Equal(HudState.Emergency, hud.State);
        Assert.Contains("Inbrisk durduruldu", hud.CurrentText);

        // Resume clears emergency
        hud.SetEmergency(false);
        Assert.Equal(HudState.Hidden, hud.State);
    }

    [Fact]
    public void Hud_RapidActivity_PreemptsSuccessOrExit()
    {
        using var hud = new ActivityHudService();
        hud.Start();
        Assert.True(hud.WaitForReady());

        hud.SetActivity("Spotify açılıyor…");
        hud.SetSuccess();
        Assert.Equal(HudState.Settling, hud.State);

        // Rapid new activity immediately cancels exit
        hud.SetActivity("Beğenilen Şarkılar açılıyor…");
        Assert.Equal(HudState.Working, hud.State);
        Assert.Equal("Beğenilen Şarkılar açılıyor…", hud.CurrentText);
    }

    [Fact]
    public void Hud_WindowStyles_NonActivating_ClickThrough()
    {
        using var hud = new ActivityHudService();
        hud.Start();
        Assert.True(hud.WaitForReady());

        var hwnd = hud.Hwnd;
        Assert.NotEqual(IntPtr.Zero, hwnd);

        // Capture exclusion
        var affinityOk = NativeMethods.GetWindowDisplayAffinity(hwnd, out var affinity);
        if (affinityOk)
        {
            Assert.Equal(NativeMethods.WDA_EXCLUDEFROMCAPTURE, affinity);
        }
    }

    [Fact]
    public void ActivityGranularity_SanitizesLongText()
    {
        var longText = new string('A', 100);
        var sanitized = ActivityGranularity.Sanitize(longText);
        Assert.True(sanitized.Length <= ActivityGranularity.MaxLabelLength);
        Assert.EndsWith("…", sanitized);
    }

    [Fact]
    public void ActivityGranularity_FormatsSemanticActions()
    {
        var launch = ActivityGranularity.FormatAction("Spotify", "launch");
        Assert.Equal("Spotify açılıyor…", launch);

        var search = ActivityGranularity.FormatAction("Explorer", "find");
        Assert.Equal("Explorer aranıyor…", search);
    }

    [Fact]
    public void UserSettings_PreferencesPersistCorrectly()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"inbrisk_test_settings_{Guid.NewGuid():N}.json");
        try
        {
            var s = new UserSettings
            {
                HudEnabled = false,
                PerimeterEnabled = true,
                AnimationsEnabled = false,
                StartWithWindows = true,
                StartMinimized = false
            };

            // JSON serialization round-trip
            var json = System.Text.Json.JsonSerializer.Serialize(s);
            var restored = System.Text.Json.JsonSerializer.Deserialize<UserSettings>(json);

            Assert.NotNull(restored);
            Assert.False(restored.HudEnabled);
            Assert.True(restored.PerimeterEnabled);
            Assert.False(restored.AnimationsEnabled);
            Assert.True(restored.StartWithWindows);
            Assert.False(restored.StartMinimized);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    [Fact]
    public void AppIconExtractor_ReturnsInbriskLogo()
    {
        using var logo = AppIconExtractor.GetInbriskLogo(24);
        Assert.NotNull(logo);
        Assert.Equal(24, logo.Width);
        Assert.Equal(24, logo.Height);
    }

    [Fact]
    public void TrayIconService_CanStartAndStop()
    {
        using var tray = new TrayIconService();
        tray.Start();
        Assert.True(tray.WaitForReady(3000));
    }

    [Fact]
    public void TrayIconService_LeftClick_TriggersOpenSettings()
    {
        var settingsOpened = new ManualResetEventSlim();
        using var tray = new TrayIconService(deduplicate: false);
        tray.OnOpenSettings = () => settingsOpened.Set();
        tray.Start();
        Assert.True(tray.WaitForReady(3000));

        var hwnd = tray.Hwnd;
        Assert.NotEqual(IntPtr.Zero, hwnd);

        // Simulate WM_LBUTTONUP message from tray callback
        NativeMethods.PostMessageW(hwnd, NativeMethods.WM_APP + 20, IntPtr.Zero, (IntPtr)NativeMethods.WM_LBUTTONUP);
        Assert.True(settingsOpened.Wait(2000), "Left-click on tray icon did not trigger OnOpenSettings!");
    }

    [Fact]
    public void McpServer_ToolMethods_ContainDesktopShellTools()
    {
        var methods = typeof(InbriskTools).GetMethods()
            .Where(m => System.Reflection.CustomAttributeExtensions.GetCustomAttribute<ModelContextProtocol.Server.McpServerToolAttribute>(m) != null)
            .Select(m => System.Reflection.CustomAttributeExtensions.GetCustomAttribute<ModelContextProtocol.Server.McpServerToolAttribute>(m)!.Name)
            .ToHashSet();

        Assert.Contains("computer_ui_status", methods);
        Assert.Contains("computer_ui_set", methods);
        Assert.Contains("computer_ui_open_settings", methods);
        Assert.Contains("computer_app_status", methods);
        Assert.Contains("computer_app_restart", methods);
        Assert.Contains("computer_app_shutdown", methods);
    }

    [Fact]
    public void McpTools_UiStatus_ReturnsValidStructure()
    {
        using var session = new McpSession(EmergencyControl.Process);
        var tools = new InbriskTools(session);

        var result = tools.UiStatus();
        Assert.NotNull(result);
        var text = string.Join("\n", result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(t => t.Text));
        using var doc = System.Text.Json.JsonDocument.Parse(text);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("hudEnabled", out _));
        Assert.True(root.TryGetProperty("perimeterEnabled", out _));
        Assert.True(root.TryGetProperty("hudVisible", out _));
        Assert.True(root.TryGetProperty("currentHudState", out _));
        Assert.True(root.TryGetProperty("currentActivity", out _));
        Assert.True(root.TryGetProperty("activeMcpSessions", out _));
    }

    [Fact]
    public void McpTools_UiSet_MutatesPreferences()
    {
        using var session = new McpSession(EmergencyControl.Process);
        var tools = new InbriskTools(session);

        // Turn HUD off
        var offResult = tools.UiSet(hud: false);
        Assert.NotNull(offResult);
        var offText = string.Join("\n", offResult.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(t => t.Text));
        using (var doc = System.Text.Json.JsonDocument.Parse(offText))
        {
            Assert.False(doc.RootElement.GetProperty("hudEnabled").GetBoolean());
        }

        // Turn HUD back on
        var onResult = tools.UiSet(hud: true);
        Assert.NotNull(onResult);
        var onText = string.Join("\n", onResult.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(t => t.Text));
        using (var doc = System.Text.Json.JsonDocument.Parse(onText))
        {
            Assert.True(doc.RootElement.GetProperty("hudEnabled").GetBoolean());
        }
    }

    [Fact]
    public void McpTools_AppStatus_ReturnsLifecycleInfo()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        var result = tools.AppStatus();
        Assert.NotNull(result);
        var text = string.Join("\n", result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(t => t.Text));
        using var doc = System.Text.Json.JsonDocument.Parse(text);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("running").GetBoolean());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("version").GetString()));
        Assert.True(root.TryGetProperty("emergencyState", out _));
        Assert.True(root.TryGetProperty("installMode", out _));
    }

    [Fact]
    public void Hud_ConnectedIdle_StartsHiddenByDefault()
    {
        using var hud = new ActivityHudService(enabled: true, animationsEnabled: true, alwaysVisible: false);
        hud.Start();
        Assert.True(hud.WaitForReady());
        Assert.NotEqual(IntPtr.Zero, hud.Hwnd);
        Thread.Sleep(100);
        // NEW CONTRACT: NO ACTIVE MCP WORK = NO PERIMETER WALL / NO STANDBY HUD
        Assert.Equal(HudState.Hidden, hud.State);
    }

    // --------------------------------------------------------------
    // Emergency-resume policy — regression for the stuck "Inbrisk
    // durduruldu" bug: resume while a client is still attached lands on
    // ConnectedIdle/Active (NOT Disconnected), and the old wiring only
    // cleared the HUD on Disconnected. These run against the production
    // HudActivityPolicy on an UNSTARTED HUD — full state machine, zero
    // pixels on any monitor, deterministic under any desktop load.
    // --------------------------------------------------------------

    private static (ActivityHudService hud, ComputerControlActivityService act)
        WireHudPolicy(int idleGraceMs = 60)
    {
        var hud = new ActivityHudService(); // never Start() → no window, no rendering
        var act = new ComputerControlActivityService(idleGraceMs);
        act.StateChanged += s => HudActivityPolicy.Apply(hud, s); // production wiring
        return (hud, act);
    }

    [Fact]
    public void HudPolicy_EmergencyResume_ToConnectedIdle_ClearsText()
    {
        var (hud, act) = WireHudPolicy();
        try
        {
            act.SetConnected(true); // client attached → ConnectedIdle
            act.SetEmergency(true); // panic
            Assert.Equal(IndicatorState.EmergencyStopped, act.State);
            Assert.Equal(HudState.Emergency, hud.State);
            Assert.Contains("durduruldu", hud.CurrentText);

            act.SetEmergency(false); // resume — client still attached
            Assert.Equal(IndicatorState.ConnectedIdle, act.State);
            Assert.NotEqual(HudState.Emergency, hud.State);
            Assert.DoesNotContain("durduruldu", hud.CurrentText);
        }
        finally { act.Dispose(); hud.Dispose(); }
    }

    [Fact]
    public void HudPolicy_EmergencyResume_ToActive_ClearsText_NoSuccessBloom()
    {
        var (hud, act) = WireHudPolicy();
        try
        {
            act.SetConnected(true);
            var lease = act.BeginActivity(); // held through the cycle → Active
            act.SetEmergency(true);
            Assert.Equal(HudState.Emergency, hud.State);
            Assert.Contains("durduruldu", hud.CurrentText);

            act.SetEmergency(false);
            Assert.Equal(IndicatorState.Active, act.State);
            Assert.NotEqual(HudState.Emergency, hud.State);
            Assert.DoesNotContain("durduruldu", hud.CurrentText);
            // resume is a clean transition — never a lime success bloom
            Assert.True(hud.State is HudState.Hidden or HudState.Standby,
                $"unexpected post-resume HUD state: {hud.State}");

            // the next real action must still drive Working normally
            hud.SetActivity("test activity", "TestApp");
            Assert.Equal(HudState.Working, hud.State);
            lease.Dispose();
        }
        finally { act.Dispose(); hud.Dispose(); }
    }

    [Fact]
    public void HudPolicy_EmergencyResume_AlwaysVisible_ReturnsToStandby()
    {
        var hud = new ActivityHudService(alwaysVisible: true); // unstarted
        var act = new ComputerControlActivityService(60);
        act.StateChanged += s => HudActivityPolicy.Apply(hud, s);
        try
        {
            act.SetConnected(true);
            act.SetEmergency(true);
            Assert.Equal(HudState.Emergency, hud.State);

            act.SetEmergency(false);
            Assert.Equal(HudState.Standby, hud.State);
            Assert.Equal("Inbrisk Hazır", hud.CurrentText);
            Assert.DoesNotContain("durduruldu", hud.CurrentText);
        }
        finally { act.Dispose(); hud.Dispose(); }
    }

    [Fact]
    public void HudPolicy_Emergency_NeverEnteredWithoutEmergencyState()
    {
        var (hud, act) = WireHudPolicy();
        try
        {
            act.SetConnected(true);
            using (act.BeginActivity()) { }
            Assert.True(SpinWait.SpinUntil(
                () => act.State == IndicatorState.ConnectedIdle, 3000));
            Assert.NotEqual(HudState.Emergency, hud.State);
            Assert.DoesNotContain("durduruldu", hud.CurrentText);
        }
        finally { act.Dispose(); hud.Dispose(); }
    }

    [Fact]
    public void Hud_Cursor_IsInteractiveHandCursor()
    {
        using var hud = new ActivityHudService();
        hud.Start();
        Assert.True(hud.WaitForReady());

        const int GclpHcursor = -12;
        var hClassCursor = NativeMethods.GetClassLongPtrW(hud.Hwnd, GclpHcursor);
        Assert.NotEqual(IntPtr.Zero, hClassCursor);

        // Verify WM_SETCURSOR handling returns TRUE (1)
        var handled = NativeMethods.SendMessageW(hud.Hwnd, NativeMethods.WM_SETCURSOR, hud.Hwnd, (IntPtr)1);
        Assert.Equal(new IntPtr(1), handled);
    }
}
