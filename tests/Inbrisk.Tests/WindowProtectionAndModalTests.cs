using System.Text.Json;
using Inbrisk.Core;
using Inbrisk.Mcp;
using Inbrisk.Platform.Windows.Topology;
using Inbrisk.Runtime;
using Inbrisk.Tests;
using Xunit;

namespace Inbrisk.Tests;

public sealed class WindowProtectionAndModalTests
{
    private sealed class MockWindowService : IWindowService
    {
        public List<WindowInfo> WindowsList { get; set; } = new();

        public IReadOnlyList<WindowInfo> ListWindows() => WindowsList;
        public WindowInfo? GetWindow(long hwnd) => WindowsList.FirstOrDefault(w => w.Hwnd == hwnd);
        public WindowInfo? GetForegroundWindow() => WindowsList.FirstOrDefault(w => w.IsForeground);
        public IReadOnlyList<MonitorInfo> GetMonitors() => [];
        public RectPx GetVirtualDesktopBounds() => new(0, 0, 1920, 1080);
        public bool FocusWindow(long hwnd) => true;
        public bool CloseWindow(long hwnd) => true;

        public WindowInfo? GetModalPopup(long hwnd)
        {
            var win = GetWindow(hwnd);
            if (win?.ModalPopupHwnd != null)
                return GetWindow(win.ModalPopupHwnd.Value);
            return WindowsList.FirstOrDefault(w => w.IsModalPopup && w.OwnerHwnd == hwnd);
        }

        public WindowInfo? GetActiveBlockingPopup(long? targetHwnd = null)
        {
            if (targetHwnd != null)
                return GetModalPopup(targetHwnd.Value);
            return WindowsList.FirstOrDefault(w => w.IsModalPopup);
        }

        public bool IsWindowEnabled(long hwnd) => GetWindow(hwnd)?.IsEnabled ?? true;

        public IReadOnlyList<WindowInfo> FindSystemDialogs() =>
            WindowsList.Where(w => w.IsModalPopup).ToList();

        public bool IsWindowProtected(long hwnd, out string? reason)
        {
            reason = null;
            var w = GetWindow(hwnd);
            if (w == null) return false;

            if (w.Pid == Environment.ProcessId)
            {
                reason = "agent host process (current pid)";
                return true;
            }

            var title = w.Title ?? "";
            bool isFileExplorer = title.Contains("File Explorer", StringComparison.OrdinalIgnoreCase) || title.Contains("Documents", StringComparison.OrdinalIgnoreCase);

            var proc = (w.ProcessName ?? "").ToLowerInvariant();
            if (!isFileExplorer && proc is "windowsterminal.exe" or "conhost.exe" or "powershell.exe" or "pwsh.exe"
                or "cmd.exe" or "code.exe" or "antigravity.exe" or "cursor.exe" or "devenv.exe" or "explorer.exe")
            {
                reason = $"protected host terminal, IDE, or system shell ('{proc}')";
                return true;
            }

            if (title.Contains("Google Colab", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Colab", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Qwen", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Antigravity", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Inbrisk", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Claude", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Jupyter", StringComparison.OrdinalIgnoreCase) ||
                title.Equals("Program Manager", StringComparison.OrdinalIgnoreCase))
            {
                reason = $"critical agent, notebook, or IDE session window ('{title}')";
                return true;
            }

            return false;
        }
    }

    [Fact]
    public void IsWindowProtected_GuardsCriticalProcessesAndTitles()
    {
        var svc = new MockWindowService();
        var qwenTerminal = new WindowInfo(0x1000, 1234, "◐︎ Qwen - inbrisk", "WindowsTerminal.exe",
            new RectPx(0, 0, 800, 600), WindowState.Normal, true, false, true, 0);
        var colabChrome = new WindowInfo(0x2000, 5678, "Google Colab - Untitled.ipynb - Google Chrome", "chrome.exe",
            new RectPx(0, 0, 800, 600), WindowState.Normal, false, false, true, 0);
        var paintApp = new WindowInfo(0x3000, 9999, "Untitled - Paint", "mspaint.exe",
            new RectPx(0, 0, 800, 600), WindowState.Normal, false, false, true, 0);

        svc.WindowsList.AddRange([qwenTerminal, colabChrome, paintApp]);

        Assert.True(svc.IsWindowProtected(0x1000, out var r1));
        Assert.Contains("protected host terminal", r1);

        Assert.True(svc.IsWindowProtected(0x2000, out var r2));
        Assert.Contains("critical agent, notebook, or IDE session window", r2);

        Assert.False(svc.IsWindowProtected(0x3000, out var r3));
        Assert.Null(r3);
    }

    [Fact]
    public void WindowInfo_FlagsModalPopupsAndBlockedWindows()
    {
        var modalPopup = new WindowInfo(0x5000, 9999, "Save Changes", "mspaint.exe",
            new RectPx(100, 100, 300, 200), WindowState.Normal, true, false, true, 0,
            IsModalPopup: true, OwnerHwnd: 0x3000);

        var paintApp = new WindowInfo(0x3000, 9999, "Untitled - Paint", "mspaint.exe",
            new RectPx(0, 0, 800, 600), WindowState.Normal, false, false, true, 0,
            ModalPopupHwnd: 0x5000, IsEnabled: false);

        var svc = new MockWindowService();
        svc.WindowsList.AddRange([modalPopup, paintApp]);

        var foundModal = svc.GetModalPopup(0x3000);
        Assert.NotNull(foundModal);
        Assert.Equal(0x5000, foundModal.Hwnd);
        Assert.Equal("Save Changes", foundModal.Title);

        Assert.False(svc.IsWindowEnabled(0x3000));
        Assert.True(svc.IsWindowEnabled(0x5000));

        var dialogs = svc.FindSystemDialogs();
        Assert.Single(dialogs);
        Assert.Equal(0x5000, dialogs[0].Hwnd);
    }

    [Fact]
    public void ProcessProvenance_LifecyclePolicy_EnforcesUserVsAgentOwnership()
    {
        var svc = new MockWindowService();
        var userChrome = new WindowInfo(0x1000, 9280, "Google Colab - Untitled.ipynb - Google Chrome", "chrome.exe",
            new RectPx(0, 0, 800, 600), WindowState.Normal, false, false, true, 0);
        var userWord = new WindowInfo(0x2000, 4444, "Document1 - Word", "winword.exe",
            new RectPx(0, 0, 800, 600), WindowState.Normal, false, false, true, 0);
        var agentNotepad = new WindowInfo(0x3000, 18432, "Untitled - Notepad", "notepad.exe",
            new RectPx(0, 0, 800, 600), WindowState.Normal, false, false, true, 0);

        svc.WindowsList.AddRange([userChrome, userWord, agentNotepad]);

        var prov = new ProcessProvenanceService(svc);

        // Pre-existing user apps discovered before task
        prov.RegisterInitial(9280, "chrome.exe", 0x1000);
        prov.RegisterInitial(4444, "winword.exe", 0x2000);

        // Agent-launched application
        prov.RegisterAgentLaunch(18432, "notepad.exe", 0x3000, "Notepad");

        // 1. Agent-launched app can be closed
        Assert.True(prov.CanAgentClose(0x3000, out var r1), r1 ?? "null");
        Assert.Null(r1);

        // 2. Pre-existing user app cannot be closed
        Assert.False(prov.CanAgentClose(0x2000, out var r2));
        Assert.Contains("ownership: user", r2);
        Assert.Contains("When in doubt, leave it open", r2);

        // 3. Even if agent interacted with pre-existing user app, ownership remains user ("kullandı != kapatabilir")
        prov.MarkUsedWindow(0x2000);
        var wordProv = prov.GetProvenanceForHwnd(0x2000);
        Assert.NotNull(wordProv);
        Assert.True(wordProv.UsedInCurrentTask);
        Assert.False(wordProv.OpenedByAgent);
        Assert.Equal(ProcessOwnership.User, wordProv.Ownership);
        Assert.False(wordProv.SafeToClose);

        Assert.False(prov.CanAgentClose(0x2000, out var r3));
        Assert.Contains("ownership: user", r3);

        // 4. Protected window (e.g. Chrome Colab) is blocked by safety policy
        Assert.False(prov.CanAgentClose(0x1000, out var r4));
        Assert.Contains("protected by safety policy", r4);

        // 5. Unknown app not registered defaults to leave it open
        Assert.False(prov.CanAgentClose(0x9999, out var r5));
        Assert.Contains("When in doubt, leave it open", r5);
    }

    [Fact]
    public void IsSystemDialogOrFlyout_DetectsModernFlyoutsAndSystemDialogs()
    {
        // 1. Modern Share flyout (ShellExperienceHost / PickerHost)
        Assert.True(WindowService.IsSystemDialogOrFlyout("ShellExperienceHost.exe", "Windows.UI.Core.CoreWindow", "Share", IntPtr.Zero));
        Assert.True(WindowService.IsSystemDialogOrFlyout("ShellExperienceHost.exe", "Windows.UI.Core.CoreWindow", "Paylaş", IntPtr.Zero));
        Assert.True(WindowService.IsSystemDialogOrFlyout("PickerHost.exe", "Windows.UI.Core.CoreWindow", "Open with", IntPtr.Zero));

        // 2. Windows Error Reporting
        Assert.True(WindowService.IsSystemDialogOrFlyout("WerFault.exe", "#32770", "Calculator has stopped working", IntPtr.Zero));

        // 3. Error / Warning titles
        Assert.True(WindowService.IsSystemDialogOrFlyout("notepad.exe", "#32770", "Notepad - Error", IntPtr.Zero));
        Assert.True(WindowService.IsSystemDialogOrFlyout("app.exe", "Window", "Beklenmeyen Hata", IntPtr.Zero));

        // 4. Standalone Win32 dialog apps without owner (e.g. Character Map) are NOT system dialogs
        Assert.False(WindowService.IsSystemDialogOrFlyout("charmap.exe", "#32770", "Character Map", IntPtr.Zero));
        // But owned dialogs ARE system/modal dialogs
        Assert.True(WindowService.IsSystemDialogOrFlyout("charmap.exe", "#32770", "Group By", new IntPtr(0x1234)));

        // 5. Regular apps should not be detected as system dialogs
        Assert.False(WindowService.IsSystemDialogOrFlyout("notepad.exe", "Notepad", "Untitled - Notepad", IntPtr.Zero));
        Assert.False(WindowService.IsSystemDialogOrFlyout("powerpnt.exe", "PPTFrameClass", "Presentation1 - PowerPoint", IntPtr.Zero));
        Assert.False(WindowService.IsSystemDialogOrFlyout("chrome.exe", "Chrome_WidgetWin_1", "Google - Google Chrome", IntPtr.Zero));

        // 6. PopupHost and lightweight menus/dropdowns are NEVER system dialogs even with owner
        Assert.False(WindowService.IsSystemDialogOrFlyout("app.exe", "PopupHost", "PopupHost", new IntPtr(0x1234)));
        Assert.False(WindowService.IsSystemDialogOrFlyout("app.exe", "Popup", "ContextMenu", new IntPtr(0x1234)));
        Assert.False(WindowService.IsSystemDialogOrFlyout("app.exe", "#32768", "", new IntPtr(0x1234)));
        Assert.False(WindowService.IsSystemDialogOrFlyout("app.exe", "ComboLBox", "", new IntPtr(0x1234)));
    }

    [Fact]
    public void ProcessProvenance_AdoptsLaunchedProcessNamesOnSubsequentWindow()
    {
        var svc = new MockWindowService();
        var epicPid = 4567;
        var epicHwnd = 0x50DA6L;

        // Window appears after launch timeout
        svc.WindowsList.Add(new WindowInfo(epicHwnd, epicPid, "Epic Games Launcher", "EpicGamesLauncher.exe",
            new RectPx(100, 100, 1024, 768), WindowState.Normal, true, false, true, 0));

        var prov = new ProcessProvenanceService(svc);
        // Agent launched EpicGamesLauncher, but window hadn't appeared yet during timeout
        prov.RegisterAgentLaunch(1234 /* bootstrap pid */, "EpicGamesLauncher", null, "Epic Games Launcher");

        // When the window is checked later, it is recognized as Agent-owned via process adoption
        Assert.True(prov.CanAgentClose(epicHwnd, out var reason), reason ?? "null");
    }

    [Fact]
    public void ProcessProvenance_TracksAgentLaunchedWindowsByHwnd()
    {
        var svc = new MockWindowService();
        // explorer.exe already running with desktop window
        var desktopHwnd = 0x10001L;
        var explorerFolderHwnd = 0xAF0CE4L;
        var explorerPid = 12004;

        svc.WindowsList.Add(new WindowInfo(desktopHwnd, explorerPid, "Program Manager", "explorer.exe",
            new RectPx(0, 0, 1920, 1080), WindowState.Normal, false, false, true, 0));
        svc.WindowsList.Add(new WindowInfo(explorerFolderHwnd, explorerPid, "Documents - File Explorer", "explorer.exe",
            new RectPx(100, 100, 800, 600), WindowState.Normal, true, false, true, 0));

        var prov = new ProcessProvenanceService(svc);
        prov.RegisterInitial(explorerPid, "explorer.exe", desktopHwnd);

        // Desktop window cannot be closed
        Assert.False(prov.CanAgentClose(desktopHwnd, out _));

        // Agent launches File Explorer folder window under same PID
        prov.RegisterAgentLaunch(explorerPid, "explorer.exe", explorerFolderHwnd, "explorer");

        // The specific agent-opened File Explorer window CAN be closed
        Assert.True(prov.CanAgentClose(explorerFolderHwnd, out var reason), reason ?? "null");
    }

    [Fact]
    public void CheckUnexpectedDialog_IgnoresStandardWindowsWithCloseButtons()
    {
        var svc = new MockWindowService();
        var mainAppHwnd = 0x10001L;
        var standardAppHwnd = 0x20002L;

        var mainApp = new WindowInfo(mainAppHwnd, 1000, "Main Editor", "editor.exe",
            new RectPx(0, 0, 800, 600), WindowState.Normal, false, false, true, 0);
        var otherApp = new WindowInfo(standardAppHwnd, 2000, "Live Captions", "livecaptions.exe",
            new RectPx(100, 100, 400, 100), WindowState.Normal, true, false, true, 0);

        svc.WindowsList.AddRange([mainApp, otherApp]);

        // Simulating element finder that returns a standard titlebar Close button
        var elements = new List<UiElement>
        {
            new UiElement("btn_close", BackendId.Uia, Role.Button, "Close", new RectPx(380, 100, 20, 20),
                ["invoke"], new Dictionary<string, object?>(),
                new ElementHandle(BackendId.Uia, "btn_close", new ReResolveRecipe(2000, standardAppHwnd, "livecaptions", Role.Button, "Close", "btn_close", [], new RectPx(380, 100, 20, 20))),
                2000, standardAppHwnd)
        };

        var waitSvc = new WaitService(spec => elements, null!, null!, new ElementRegistry([]), svc);

        // Standard window with just a Close button must NOT be flagged as an unexpected dialog
        var diag = waitSvc.CheckUnexpectedDialog(mainAppHwnd);
        Assert.Null(diag);
    }

    [Fact]
    public void CheckUnexpectedDialog_DetectsActiveBlockingPopups()
    {
        var svc = new MockWindowService();
        var mainAppHwnd = 0x10001L;
        var modalDialogHwnd = 0x30003L;

        var mainApp = new WindowInfo(mainAppHwnd, 1000, "Main Editor", "editor.exe",
            new RectPx(0, 0, 800, 600), WindowState.Normal, false, false, true, 0);
        var modalDialog = new WindowInfo(modalDialogHwnd, 1000, "Save Changes?", "editor.exe",
            new RectPx(200, 200, 300, 150), WindowState.Normal, true, false, true, 0,
            IsModalPopup: true, OwnerHwnd: mainAppHwnd);

        svc.WindowsList.AddRange([mainApp, modalDialog]);

        var waitSvc = new WaitService(_ => [], null!, null!, new ElementRegistry([]), svc);

        var diag = waitSvc.CheckUnexpectedDialog(mainAppHwnd);
        Assert.NotNull(diag);
        Assert.Equal(modalDialogHwnd, diag.Value.Hwnd);
        Assert.Equal("Save Changes?", diag.Value.Title);
    }
}
