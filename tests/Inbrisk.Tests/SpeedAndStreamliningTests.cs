using System.Diagnostics;
using System.Text.Json;
using Inbrisk.Core;
using Inbrisk.Mcp;
using Inbrisk.Platform.Windows.Apps;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Inbrisk.Tests;

public class SpeedAndStreamliningTests
{
    private sealed class FakeWindows : IWindowService
    {
        public List<WindowInfo> Windows { get; } = new();
        public IReadOnlyList<WindowInfo> ListWindows() => Windows;
        public WindowInfo? GetWindow(long hwnd) => Windows.FirstOrDefault(w => w.Hwnd == hwnd);
        public WindowInfo? GetForegroundWindow() => Windows.FirstOrDefault(w => w.IsForeground);
        public IReadOnlyList<MonitorInfo> GetMonitors() => [];
        public RectPx GetVirtualDesktopBounds() => new(0, 0, 1920, 1080);
        public bool FocusWindow(long hwnd) => true;
        public bool CloseWindow(long hwnd)
        {
            var idx = Windows.FindIndex(w => w.Hwnd == hwnd);
            if (idx >= 0)
            {
                Windows.RemoveAt(idx);
                return true;
            }
            return false;
        }
        public WindowInfo? GetModalPopup(long hwnd) => null;
        public bool IsWindowEnabled(long hwnd) => true;
        public IReadOnlyList<WindowInfo> FindSystemDialogs() => [];
        public bool IsWindowProtected(long hwnd, out string? reason)
        {
            if (hwnd == 0xDEAD)
            {
                reason = "Protected host IDE";
                return true;
            }
            reason = null;
            return false;
        }
    }

    [Fact]
    public void AppService_ListApps_UsesCacheOnSubsequentCalls()
    {
        var win = new FakeWindows();
        var svc = new AppService(win);
        svc.InvalidateCache();

        int packageCalls = 0;
        svc.PackageEnumerator = () =>
        {
            packageCalls++;
            return new List<AppService.ResolvedApp>
            {
                new(LaunchMethod.Aumid, "Test.App!App", "Test App", ["TestApp"], 100)
            };
        };

        // First call populates cache
        var firstList = svc.ListApps();
        Assert.NotNull(firstList);
        Assert.Equal(1, packageCalls);

        // Second call must return cached list without calling PackageEnumerator again
        var secondList = svc.ListApps();
        Assert.NotNull(secondList);
        Assert.Equal(1, packageCalls);
        Assert.Same(firstList, secondList);
    }

    [Fact]
    public void AppService_EnumeratePackages_UsesCache()
    {
        var win = new FakeWindows();
        var svc = new AppService(win);
        svc.InvalidateCache();

        var packages1 = svc.EnumeratePackages();
        var packages2 = svc.EnumeratePackages();

        Assert.NotNull(packages1);
        Assert.Same(packages1, packages2);
    }

    [Fact]
    public async Task CloseWindow_BatchMode_CloseAllAgentWindows()
    {
        var session = new McpSession(EmergencyControl.Process);
        var tools = new InbriskTools(session);

        // Record a mock agent-owned window in provenance
        var testHwnd = 0x12345L;
        session.Rt.Provenance.RegisterAgentLaunch(1001, "calc.exe", testHwnd);

        // Calling CloseWindow with closeAllAgentWindows: true
        var res = await tools.CloseWindow(closeAllAgentWindows: true);
        Assert.NotNull(res);

        using var doc = JsonDocument.Parse(((TextContentBlock)res.Content[0]).Text);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("batch").GetBoolean());
    }

    [Fact]
    public async Task CloseWindow_BatchMode_WithHwndsList()
    {
        var session = new McpSession(EmergencyControl.Process);
        var tools = new InbriskTools(session);

        // Target fake hwnds
        var hwnds = new[] { "0x99999", "0x88888" };
        var res = await tools.CloseWindow(hwnds: hwnds);
        Assert.NotNull(res);

        using var doc = JsonDocument.Parse(((TextContentBlock)res.Content[0]).Text);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("batch").GetBoolean());
        Assert.Equal(2, doc.RootElement.GetProperty("totalRequested").GetInt32());
    }

    [Fact]
    public async Task ComputerDo_ValidatesMalformedWhenNoActionsProvided()
    {
        var session = new McpSession(EmergencyControl.Process);
        var tools = new InbriskTools(session);

        var res = await tools.ComputerDo();
        Assert.NotNull(res);

        using var doc = JsonDocument.Parse(((TextContentBlock)res.Content[0]).Text);
        Assert.Equal("Malformed", doc.RootElement.GetProperty("error").GetString());
    }
}
