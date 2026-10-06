using System.Diagnostics;
using System.Text.Json;
using Inbrisk.Core;
using Inbrisk.Platform.Windows.Apps;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Inbrisk.Tests;

/// <summary>
/// AppService unit tests — fake IWindowService + Spawner/PackageEnumerator
/// seams make the resolution pipeline deterministic; no real processes are
/// spawned unless a test says so. Real-machine integration lives in the
/// desktop collection further down.
/// </summary>
public sealed class LaunchUnitTests
{
    private sealed class FakeWindows : IWindowService
    {
        public List<WindowInfo> Windows { get; } = new();
        public IReadOnlyList<WindowInfo> ListWindows() => Windows;
        public WindowInfo? GetWindow(long hwnd) =>
            Windows.FirstOrDefault(w => w.Hwnd == hwnd);
        public WindowInfo? GetForegroundWindow() =>
            Windows.FirstOrDefault(w => w.IsForeground);
        public IReadOnlyList<MonitorInfo> GetMonitors() => [];
        public RectPx GetVirtualDesktopBounds() => new(0, 0, 1920, 1080);
        public bool FocusWindow(long hwnd) => true;
        public bool CloseWindow(long hwnd) => true;
        public WindowInfo? GetModalPopup(long hwnd) => null;
        public bool IsWindowEnabled(long hwnd) => true;
        public IReadOnlyList<WindowInfo> FindSystemDialogs() => [];
        public bool IsWindowProtected(long hwnd, out string? reason) { reason = null; return false; }
    }

    private static WindowInfo Win(long hwnd, int pid, string title,
        string process, int w = 800, int h = 600) =>
        new(hwnd, pid, title, process, new RectPx(10, 10, w, h),
            WindowState.Normal, false, false, true, 0);

    private static (AppService svc, FakeWindows win, List<(string Method,
        string Identifier)> spawns) Svc()
    {
        var win = new FakeWindows();
        var spawns = new List<(string, string)>();
        var svc = new AppService(win)
        {
            Spawner = (app, args) =>
            {
                spawns.Add((app.Method.ToString(), app.Identifier));
                return Process.GetCurrentProcess().Id; // alive pid
            },
        };
        return (svc, win, spawns);
    }

    private static LaunchSpec For(string app, string waitFor = "window",
        int timeoutMs = 1500) =>
        new(App: app, WaitFor: waitFor, TimeoutMs: timeoutMs);

    // ---------- resolution ----------

    [Fact]
    public void AlreadyRunning_ReusesWindow_NoSpawn()
    {
        var (svc, win, spawns) = Svc();
        win.Windows.Add(Win(0x1001, 4242, "Spotify Premium", "Spotify"));
        var r = svc.Launch(For("Spotify"));
        Assert.True(r.Success);
        Assert.Equal(LaunchMethod.ExistingInstance, r.Method);
        Assert.Equal("AlreadyRunning", r.LaunchState);
        Assert.Equal(0x1001, r.Hwnd);
        Assert.Equal(4242, r.Pid);
        Assert.Empty(spawns);
    }

    [Fact]
    public void AlreadyRunning_NewInstance_Spawns()
    {
        var (svc, win, spawns) = Svc();
        win.Windows.Add(Win(0x1001, 4242, "Spotify Premium", "Spotify"));
        var r = svc.Launch(new LaunchSpec(App: "Spotify", NewInstance: true,
            WaitFor: "none"));
        // Spotify resolves on this machine (StartMenu/AppPath/AUMID/PATH);
        // whatever it resolved to, it must have spawned, not reused
        if (r.Error == "TargetNotFound") return; // app absent on this box
        Assert.True(r.Success, r.ErrorDetail);
        Assert.NotEqual(LaunchMethod.ExistingInstance, r.Method);
        Assert.Single(spawns);
    }

    [Fact]
    public void MultiProcess_PicksUserFacingWindow()
    {
        var (svc, win, _) = Svc();
        // a helper process owns a title-less zero window; the user-facing
        // window belongs to a different pid — the window is the authority
        win.Windows.Add(Win(0x2001, 9001, "", "Spotify", 1, 1));
        win.Windows.Add(Win(0x2002, 9002, "Spotify Free", "Spotify"));
        var r = svc.Launch(For("Spotify"));
        Assert.True(r.Success);
        Assert.Equal(0x2002, r.Hwnd);
        Assert.Equal(9002, r.Pid);
    }

    [Fact]
    public void LauncherHandoff_MatchesChildProcessWindow()
    {
        var (svc, win, _) = Svc();
        // A launcher like blender-launcher spawns and exits; child process has different PID and bare name
        win.Windows.Add(Win(0x3001, 8888, "(Unsaved) - Blender 5.2.0 LTS", "blender.exe"));
        svc.PackageEnumerator = () =>
        [
            new AppService.ResolvedApp(LaunchMethod.StartMenu,
                @"C:\Path\Blender 5.2.lnk", "Blender 5.2", ["Blender 5.2", "blender-launcher", "blender"], 100)
        ];
        var r = svc.Launch(For("Blender"));
        Assert.True(r.Success, r.ErrorDetail);
        Assert.Equal(0x3001, r.Hwnd);
        Assert.Equal(8888, r.Pid);
    }

    [Fact]
    public void TokenMatch_BeatsCompoundSubstring_EdgeWinsOverGoAwayEdge()
    {
        var (svc, win, spawns) = Svc();
        svc.PackageEnumerator = () =>
        [
            new AppService.ResolvedApp(LaunchMethod.StartMenu,
                @"C:\Path\Microsoft Edge.lnk", "Microsoft Edge", ["msedge"], 0),
            new AppService.ResolvedApp(LaunchMethod.StartMenu,
                @"C:\Path\GoAwayEdge.lnk", "GoAwayEdge", ["GoAwayEdge"], 0)
        ];
        win.Windows.Add(Win(0x4001, 7777, "New Tab - Microsoft Edge", "msedge.exe"));

        var r = svc.Launch(For("Edge"));
        Assert.True(r.Success, r.ErrorDetail);
        Assert.Equal(0x4001, r.Hwnd);
        Assert.Equal(7777, r.Pid);
    }

    [Fact]
    public void UwpLocalizedAlias_MatchesApplicationFrameHost()
    {
        var (svc, win, spawns) = Svc();
        // Packaged app for Settings in Turkish Windows is Ayarlar
        svc.PackageEnumerator = () =>
        [
            new AppService.ResolvedApp(LaunchMethod.Aumid,
                "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel",
                "Ayarlar", ["SystemSettings"], 0)
        ];
        // UWP window is hosted under ApplicationFrameHost.exe with localized title "Ayarlar"
        win.Windows.Add(Win(0x5001, 6666, "Ayarlar", "ApplicationFrameHost.exe"));

        var r = svc.Launch(For("Settings"));
        Assert.True(r.Success, r.ErrorDetail);
        Assert.Equal(0x5001, r.Hwnd);
        Assert.Equal(6666, r.Pid);
    }

    [Fact]
    public void AmbiguousName_ReturnsCandidates_NeverGuesses()
    {
        var (svc, _, spawns) = Svc();
        svc.PackageEnumerator = () =>
        [
            new AppService.ResolvedApp(LaunchMethod.Aumid,
                "Contoso.ZxqwTest_abc!Alpha", "ZxqwTest Alpha", [], 0),
            new AppService.ResolvedApp(LaunchMethod.Aumid,
                "Contoso.ZxqwTest_abc!Beta", "ZxqwTest Beta", [], 0),
        ];
        var r = svc.Launch(For("ZxqwTest"));
        Assert.False(r.Success);
        Assert.Equal("AmbiguousApplication", r.Error);
        Assert.NotNull(r.Candidates);
        Assert.Equal(2, r.Candidates!.Count);
        Assert.Empty(spawns); // never spawned a guess
    }

    [Fact]
    public void UnknownApp_TargetNotFound()
    {
        var (svc, _, spawns) = Svc();
        svc.PackageEnumerator = () => [];
        var r = svc.Launch(For("zxqwvbnm_no_such_app_42"));
        Assert.False(r.Success);
        Assert.Equal("TargetNotFound", r.Error);
        Assert.Empty(spawns);
    }

    [Fact]
    public void InvalidWaitFor_Malformed()
    {
        var (svc, _, _) = Svc();
        var r = svc.Launch(new LaunchSpec(App: "notepad", WaitFor: "moon"));
        Assert.False(r.Success);
        Assert.Equal("Malformed", r.Error);
    }

    // ---------- readiness ----------

    [Fact]
    public void WaitForWindow_TimesOut_WhenNoWindowAppears()
    {
        var (svc, _, _) = Svc();
        svc.PackageEnumerator = () =>
        [
            new AppService.ResolvedApp(LaunchMethod.Aumid,
                "Test.Ghost_abc!App", "TestGhost", ["ghost"], 0),
        ];
        var r = svc.Launch(new LaunchSpec(App: "TestGhost",
            WaitFor: "window", TimeoutMs: 700));
        Assert.False(r.Success);
        Assert.Equal("Timeout", r.Error);
        Assert.Equal("TimedOut", r.LaunchState);
    }

    [Fact]
    public void WaitForProcess_Succeeds_WithoutWindow()
    {
        var (svc, _, _) = Svc();
        svc.PackageEnumerator = () =>
        [
            new AppService.ResolvedApp(LaunchMethod.Aumid,
                "Test.Ghost_abc!App", "TestGhost", [], 0),
        ];
        var r = svc.Launch(new LaunchSpec(App: "TestGhost",
            WaitFor: "process", TimeoutMs: 1500));
        Assert.True(r.Success, r.ErrorDetail);
        Assert.Equal("ProcessStarted", r.LaunchState);
        Assert.Null(r.Hwnd);
    }

    [Fact]
    public void WaitForNone_ReturnsImmediately()
    {
        var (svc, _, _) = Svc();
        svc.PackageEnumerator = () =>
        [
            new AppService.ResolvedApp(LaunchMethod.Aumid,
                "Test.Ghost_abc!App", "TestGhost", [], 0),
        ];
        var r = svc.Launch(new LaunchSpec(App: "TestGhost",
            WaitFor: "none"));
        Assert.True(r.Success, r.ErrorDetail);
        Assert.Equal("Started", r.LaunchState);
    }

    [Fact]
    public void WindowAppearingDuringWait_Resolves()
    {
        var (svc, win, _) = Svc();
        svc.Spawner = (_, _) =>
        {
            // window materializes on the next poll
            Task.Run(async () =>
            {
                await Task.Delay(250);
                win.Windows.Add(Win(0x3001, 7777, "Ghost", "ghost"));
            });
            return 7777;
        };
        svc.PackageEnumerator = () =>
        [
            new AppService.ResolvedApp(LaunchMethod.Aumid,
                "Test.Ghost_abc!App", "TestGhost", ["ghost"], 0),
        ];
        var r = svc.Launch(new LaunchSpec(App: "TestGhost",
            WaitFor: "window", TimeoutMs: 3000));
        Assert.True(r.Success, r.ErrorDetail);
        Assert.Equal("Ready", r.LaunchState);
        Assert.Equal(0x3001, r.Hwnd);
        Assert.Equal(7777, r.Pid);
    }

    // ---------- security ----------

    [Theory]
    [InlineData("cmd.exe")]
    [InlineData("powershell.exe")]
    [InlineData("pwsh.exe")]
    [InlineData("mshta.exe")]
    [InlineData("rundll32.exe")]
    public void ShellInterpreter_PolicyDenied(string exe)
    {
        var (svc, _, spawns) = Svc();
        var r = svc.Launch(new LaunchSpec(Executable: exe, WaitFor: "none"));
        Assert.False(r.Success);
        Assert.Equal("PolicyDenied", r.Error);
        Assert.Empty(spawns);
    }

    [Fact]
    public void ExplicitPath_ToShell_PolicyDenied()
    {
        var (svc, _, spawns) = Svc();
        var cmd = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.System), "cmd.exe");
        if (!File.Exists(cmd)) return;
        var r = svc.Launch(new LaunchSpec(Path: cmd, WaitFor: "none"));
        Assert.False(r.Success);
        Assert.Equal("PolicyDenied", r.Error);
        Assert.Empty(spawns);
    }

    [Fact]
    public void Arguments_PassedAsStructuredArray_NotCommandLine()
    {
        var (svc, _, _) = Svc();
        string[]? captured = null;
        svc.Spawner = (app, args) =>
        {
            captured = args?.ToArray();
            return Process.GetCurrentProcess().Id;
        };
        var notepad = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.System), "notepad.exe");
        if (!File.Exists(notepad)) return;
        var r = svc.Launch(new LaunchSpec(Path: notepad,
            Arguments: ["C:\\temp\\a file.txt", "--flag with space"],
            WaitFor: "none"));
        Assert.True(r.Success, r.ErrorDetail);
        Assert.Equal(["C:\\temp\\a file.txt", "--flag with space"], captured);
    }

    [Fact]
    public void Arguments_OnUri_Malformed()
    {
        var (svc, _, _) = Svc();
        var r = svc.Launch(new LaunchSpec(Uri: "ms-settings:",
            Arguments: ["x"], WaitFor: "none"));
        Assert.False(r.Success);
        Assert.Equal("Malformed", r.Error);
    }

    [Fact]
    public void Cancellation_Throws()
    {
        var (svc, _, _) = Svc();
        var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(
            () => svc.Launch(For("whatever", waitFor: "none"), cts.Token));
    }
}

/// <summary>
/// Real-machine launch tests through the shared InbriskRuntime — they
/// exercise the actual registry/PATH/UIA readiness pipeline. Anything that
/// spawns a real process cleans it up; anything machine-dependent degrades
/// gracefully (e.g. no Calculator package → no-op, not failure).
/// </summary>
[Collection("desktop")]
public sealed class LaunchIntegrationTests
{
    private readonly DesktopFixture _fx;
    public LaunchIntegrationTests(DesktopFixture fx) => _fx = fx;

    private static void KillNew(string processName, HashSet<int> before)
    {
        foreach (var p in Process.GetProcessesByName(processName))
        {
            if (before.Contains(p.Id)) continue;
            try { p.Kill(); } catch { }
        }
    }

    [Fact]
    public void Notepad_FriendlyName_LaunchAndReuse()
    {
        using var tracker = new TestProcessTracker("notepad");
        var r = _fx.Inbrisk.Launch(new LaunchSpec(App: "notepad"));
        Assert.True(r.Success, r.ErrorDetail);
        Assert.Contains(r.Method!.Value, new[]
        {
            LaunchMethod.Executable, LaunchMethod.AppPath,
            LaunchMethod.Aumid, LaunchMethod.ExistingInstance,
        });
        if (r.LaunchState == "AlreadyRunning") return; // was open already

        Assert.Equal("Ready", r.LaunchState);
        Assert.NotNull(r.Pid);
        Assert.NotNull(r.Hwnd);
        Assert.False(string.IsNullOrEmpty(r.WindowTitle));

        // second call reuses — never double-spawns
        var r2 = _fx.Inbrisk.Launch(new LaunchSpec(App: "notepad"));
        Assert.True(r2.Success, r2.ErrorDetail);
        Assert.Equal("AlreadyRunning", r2.LaunchState);
        Assert.Equal(r.Hwnd, r2.Hwnd);
    }

    [Fact]
    public void Notepad_ByExecutable_Launches()
    {
        using var tracker = new TestProcessTracker("notepad");
        var r = _fx.Inbrisk.Launch(new LaunchSpec(Executable: "notepad.exe"));
        Assert.True(r.Success, r.ErrorDetail);
        Assert.NotNull(r.Hwnd);
    }

    [Fact]
    public void Calculator_Aumid_Launch()
    {
        using var tracker = new TestProcessTracker("CalculatorApp", "calculator");
        // packaged-app path — skipped silently on machines without it
        var svc = _fx.Inbrisk.Parts.Apps;
        var cands = svc.ResolveCandidates("calculator")
            .Where(c => c.Method == LaunchMethod.Aumid).ToList();
        if (cands.Count == 0) return;
        var r = _fx.Inbrisk.Launch(new LaunchSpec(App: "calculator"));
        if (r.Pid.HasValue) tracker.TrackPid(r.Pid.Value);
        Assert.True(r.Success, r.ErrorDetail);
        Assert.NotNull(r.Hwnd);
    }

    [Fact]
    public void PackageEnumeration_FindsInstalledApps_WithAumids()
    {
        // regression guard for the packaged-app source: enumeration must
        // cover per-user AND inbox apps, with usable PFN!AppId identifiers
        var svc = (Inbrisk.Platform.Windows.Apps.AppService)_fx.Inbrisk.Parts.Apps;
        var pkgs = svc.EnumeratePackages();
        Assert.True(pkgs.Count > 10,
            $"package enumeration suspiciously small: {pkgs.Count}");
        Assert.All(pkgs, p =>
        {
            Assert.Contains("!", p.Identifier);      // PFN!AppId shape
            Assert.False(string.IsNullOrWhiteSpace(p.DisplayName));
        });
        // at least one well-known inbox app exists on any Windows 10/11 box
        Assert.Contains(pkgs, p => p.Identifier.Contains(
            "Microsoft.", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EmergencyStop_DeniesLaunch()
    {
        _fx.Inbrisk.Policy.KillSwitch = true;
        try
        {
            var r = _fx.Inbrisk.Launch(new LaunchSpec(App: "notepad"));
            Assert.False(r.Success);
            Assert.Equal("EmergencyStopped", r.Error);
        }
        finally { _fx.Inbrisk.Policy.KillSwitch = false; }
    }

    [Fact]
    public async Task ActivityIndicator_ActiveDuringLaunchWait()
    {
        // where.exe is a real console exe that never opens a window —
        // launch sits in the readiness wait while we observe the indicator.
        // State is only Active when a client is connected; ActiveLeases is
        // the connection-independent truth.
        var where = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.System), "where.exe");
        if (!File.Exists(where)) return;
        var seenActive = new TaskCompletionSource();
        var sawLease = false;
        void OnState(IndicatorState s)
        { if (s == IndicatorState.Active) seenActive.TrySetResult(); }
        _fx.Inbrisk.Activity.StateChanged += OnState;
        _fx.Inbrisk.Activity.SetConnected(true);
        try
        {
            var task = Task.Run(() => _fx.Inbrisk.Launch(new LaunchSpec(
                Path: where, WaitFor: "window", TimeoutMs: 1500)));
            while (!task.IsCompleted)
            {
                if (_fx.Inbrisk.Activity.ActiveLeases > 0) { sawLease = true; break; }
                await Task.Delay(25);
            }
            var r = await task;
            await Task.WhenAny(seenActive.Task, Task.Delay(500));
            Assert.True(sawLease, "launch never held an activity lease");
            Assert.True(seenActive.Task.IsCompleted,
                "indicator never showed Active");
            Assert.Equal("Timeout", r.Error); // launched but no window
        }
        finally
        {
            _fx.Inbrisk.Activity.StateChanged -= OnState;
            _fx.Inbrisk.Activity.SetConnected(false);
        }
    }

    [Fact]
    public void ResolveCandidates_Notepad_FindsSomething()
    {
        var cands = _fx.Inbrisk.Parts.Apps.ResolveCandidates("notepad");
        Assert.NotEmpty(cands);
        Assert.Contains(cands, c =>
            c.Method is LaunchMethod.AppPath or LaunchMethod.Executable
                or LaunchMethod.StartMenu or LaunchMethod.Aumid);
    }
}

/// <summary>
/// MCP wire-level launch tests: the real stdio protocol, real desktop.
/// computer_launch must exist, validate strictly, deny under emergency
/// stop, and the computer_run launch step must bind a usable window scope.
/// </summary>
[Collection("McpStdioProcess")]
public sealed class LaunchMcpTests
{
    private static async Task<McpClient> ConnectAsync()
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "inbrisk-mcp.dll");
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = "dotnet",
            Arguments = [dll],
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["INBRISK_PANIC_HOTKEY"] = "Ctrl+Alt+F8",
                ["INBRISK_RESUME_HOTKEY"] = "Ctrl+Alt+Shift+F8",
                ["INBRISK_EMERGENCY_STATE"] = Path.Combine(Path.GetTempPath(),
                    $"inbrisk-launchtest-{Guid.NewGuid():N}.flag"),
            },
        });
        return await McpClient.CreateAsync(transport);
    }

    private static string Text(CallToolResult r)
        => string.Join("\n", r.Content.OfType<TextContentBlock>().Select(t => t.Text));

    private static JsonDocument Json(CallToolResult r)
        => JsonDocument.Parse(Text(r));

    [Fact]
    public async Task Tool_Listed_And_Validates()
    {
        await using var client = await ConnectAsync();
        var tools = await client.ListToolsAsync();
        Assert.Contains(tools, t => t.Name == "computer_launch");

        var noId = await client.CallToolAsync("computer_launch",
            new Dictionary<string, object?>());
        Assert.True(noId.IsError);
        Assert.Contains("Malformed", Text(noId));

        var twoIds = await client.CallToolAsync("computer_launch",
            new Dictionary<string, object?>
            {
                ["app"] = "notepad", ["path"] = "C:\\x.exe",
            });
        Assert.True(twoIds.IsError);
        Assert.Contains("Malformed", Text(twoIds));
    }

    [Fact]
    public async Task Launch_Notepad_Verified_ThenReuse()
    {
        using var tracker = new TestProcessTracker("notepad");
        await using var client = await ConnectAsync();
        var r = Json(await client.CallToolAsync("computer_launch",
            new Dictionary<string, object?> { ["app"] = "notepad" }));
        var root = r.RootElement;
        Assert.Equal("Verified", root.GetProperty("status").GetString());
        Assert.Equal("notepad", root.GetProperty("app").GetString());
        Assert.True(root.GetProperty("durationMs").GetInt64() > 0);
        var state = root.GetProperty("launchState").GetString();
        Assert.Contains(state, new[] { "Ready", "AlreadyRunning" });
        if (state == "Ready")
        {
            Assert.StartsWith("0x", root.GetProperty("window")
                .GetProperty("hwnd").GetString()!);
            Assert.False(string.IsNullOrEmpty(root.GetProperty("window")
                .GetProperty("title").GetString()));
        }

        // second call reuses — never double-spawns
        var r2 = Json(await client.CallToolAsync("computer_launch",
            new Dictionary<string, object?> { ["app"] = "notepad" }));
        Assert.Equal("AlreadyRunning",
            r2.RootElement.GetProperty("launchState").GetString());
    }

    [Fact]
    public async Task Launch_ShellInterpreter_PolicyDenied()
    {
        await using var client = await ConnectAsync();
        var r = await client.CallToolAsync("computer_launch",
            new Dictionary<string, object?>
            {
                ["executable"] = "powershell.exe",
                ["arguments"] = new[] { "-Command", "echo hi" },
                ["waitFor"] = "none",
            });
        Assert.True(r.IsError);
        Assert.Contains("PolicyDenied", Text(r));
    }

    [Fact]
    public async Task Launch_NoShellSmuggling_ThroughArgs()
    {
        // even a benign exe must never turn arguments into a command line —
        // and an explicit path to a shell host is denied outright
        await using var client = await ConnectAsync();
        var r = await client.CallToolAsync("computer_launch",
            new Dictionary<string, object?>
            {
                ["path"] = Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.System), "cmd.exe"),
                ["arguments"] = new[] { "/c", "calc" },
                ["waitFor"] = "none",
            });
        Assert.True(r.IsError);
        Assert.Contains("PolicyDenied", Text(r));
    }

    [Fact]
    public async Task Run_LaunchStep_BindsWindowScope()
    {
        using var tracker = new TestProcessTracker("notepad");
        await using var client = await ConnectAsync();
        var steps = new object[]
        {
            new Dictionary<string, object?>
            {
                ["action"] = "launch", ["app"] = "notepad",
                ["as"] = "np",
            },
            // the bound hwnd scopes the find to the app's window —
            // any element inside it proves the scope held (Win11's
            // editor surface isn't role:"Edit" on every build)
            new Dictionary<string, object?>
            {
                ["action"] = "find",
                ["target"] = new Dictionary<string, object?>
                {
                    ["within"] = "$np",
                },
                ["select"] = "first", ["as"] = "inner",
            },
        };
        var r = await client.CallToolAsync("computer_run",
            new Dictionary<string, object?> { ["steps"] = steps });
        var txt = Text(r);
        Assert.False(r.IsError, txt);
        using var doc = Json(r);
        Assert.Equal("Completed",
            doc.RootElement.GetProperty("status").GetString());
    }
}
