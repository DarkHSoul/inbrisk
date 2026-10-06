using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Inbrisk.Core;
using Inbrisk.Platform.Windows.Apps;
using Xunit;

namespace Inbrisk.Tests;

/// <summary>
/// Phase H Round 4: Application Profiles & App Knowledge Tests.
/// Validates:
/// 1. Profile persistence of stable landmarks across runtime restarts.
/// 2. App version change invalidating incompatible selectors while preserving version-agnostic metadata.
/// 3. Selector failure evicting ONLY the affected landmark and self-healing.
/// 4. Strict prohibition of live UI state (HWND, COM objects, current bounds, live values) in persistent profiles.
/// 5. Atomic write/swap semantics preventing corruption under concurrent writers.
/// 6. Stale writer rejection protecting newer validated knowledge.
/// 7. Interoperability with the existing recipe system (TaskRecipeDefinition / ITaskRecipeStore).
/// 8. Constrained deep links preventing arbitrary shell command execution.
/// 9. Compact launch app-map incorporating profile knowledge.
/// 10. Canonical plan executor reuse for post-launch continuation steps.
/// 11. Unexpected modal handling returning a single meaningful pause rather than failing catastrophically.
/// </summary>
public sealed class PhaseHRound4AppKnowledgeTests : IDisposable
{
    private readonly string _tempDir;

    public PhaseHRound4AppKnowledgeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"inbrisk-h4-knowledge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { }
    }

    private sealed class FakeWindowService : IWindowService
    {
        public List<WindowInfo> Windows { get; } = new();
        public WindowInfo? ModalToReturn { get; set; }

        public IReadOnlyList<WindowInfo> ListWindows() => Windows;
        public WindowInfo? GetWindow(long hwnd) => Windows.FirstOrDefault(w => w.Hwnd == hwnd);
        public WindowInfo? GetForegroundWindow() => Windows.FirstOrDefault(w => w.IsForeground);
        public IReadOnlyList<MonitorInfo> GetMonitors() => Array.Empty<MonitorInfo>();
        public RectPx GetVirtualDesktopBounds() => new(0, 0, 1920, 1080);
        public bool FocusWindow(long hwnd) => true;
        public bool CloseWindow(long hwnd) => true;
        public WindowInfo? GetModalPopup(long hwnd) => ModalToReturn;
        public bool IsWindowEnabled(long hwnd) => ModalToReturn == null;
        public IReadOnlyList<WindowInfo> FindSystemDialogs() => ModalToReturn != null ? new[] { ModalToReturn } : Array.Empty<WindowInfo>();
        public bool IsWindowProtected(long hwnd, out string? reason) { reason = null; return false; }
    }

    private sealed class FakeAppService : IAppService
    {
        public Func<LaunchSpec, LaunchResult>? OnLaunch { get; set; }

        public LaunchResult Launch(LaunchSpec spec, CancellationToken ct = default)
        {
            if (OnLaunch != null)
                return OnLaunch(spec);

            return new LaunchResult(
                Success: true,
                Method: LaunchMethod.Executable,
                ResolvedName: spec.App ?? "TestApp",
                ResolvedIdentifier: "C:\\Program Files\\TestApp\\testapp.exe",
                Pid: 1234,
                Hwnd: 0x1001,
                WindowTitle: "Test Application Main Window",
                LaunchState: "Ready",
                LaunchMs: 15,
                ReadyMs: 25,
                Error: null);
        }

        public IReadOnlyList<AppCandidate> ResolveCandidates(string app) => Array.Empty<AppCandidate>();
        public IReadOnlyList<AppInfo> ListApps() => Array.Empty<AppInfo>();
        public IReadOnlyList<string> FindExecutables(string query) => Array.Empty<string>();
    }

    private sealed class InMemoryTaskRecipeStore : ITaskRecipeStore
    {
        private readonly ConcurrentDictionary<string, TaskRecipeDefinition> _recipes = new(StringComparer.OrdinalIgnoreCase);

        public void Save(TaskRecipeDefinition recipe) => _recipes[recipe.Name] = recipe;
        public TaskRecipeDefinition? Get(string name) => _recipes.TryGetValue(name, out var r) ? r : null;
        public IReadOnlyList<TaskRecipeDefinition> List() => _recipes.Values.ToList();
        public void RecordRun(string name, bool success) { }
    }

    // =========================================================================
    // 1. Profile_PersistsStableLandmarkAcrossRuntimeRestart
    // =========================================================================
    [Fact]
    public void Profile_PersistsStableLandmarkAcrossRuntimeRestart()
    {
        var identity = new ApplicationIdentity(
            canonicalExecutablePath: "C:\\Program Files\\Notepad++\\notepad++.exe",
            displayName: "Notepad++");

        // Runtime Instance 1: Create profile and save stable landmark knowledge
        var store1 = new ApplicationProfileStore(_tempDir);
        var profile1 = new ApplicationProfile
        {
            Identity = identity,
            AppVersion = "8.5.8"
        };

        var landmark = new LandmarkKnowledge
        {
            Name = "EditorScintilla",
            Selector = new LandmarkSelector
            {
                Role = "Edit",
                AutomationId = "1001",
                ClassName = "Scintilla"
            },
            Validated = true,
            LastValidatedAtUtc = DateTimeOffset.UtcNow,
            Confidence = 0.98
        };
        profile1.AddOrUpdateLandmark(landmark);
        store1.SaveProfile(profile1);

        // Runtime Instance 2: Simulate restart by constructing a fresh store pointing to same directory
        var store2 = new ApplicationProfileStore(_tempDir);
        var loadedProfile = store2.GetProfile(identity);

        Assert.NotNull(loadedProfile);
        Assert.Equal("8.5.8", loadedProfile.AppVersion);
        Assert.True(loadedProfile.Landmarks.ContainsKey("EditorScintilla"));

        var loadedLandmark = loadedProfile.Landmarks["EditorScintilla"];
        Assert.Equal("EditorScintilla", loadedLandmark.Name);
        Assert.Equal("Edit", loadedLandmark.Selector.Role);
        Assert.Equal("1001", loadedLandmark.Selector.AutomationId);
        Assert.Equal("Scintilla", loadedLandmark.Selector.ClassName);
        Assert.True(loadedLandmark.Validated);
        Assert.Equal(0.98, loadedLandmark.Confidence);
    }

    // =========================================================================
    // 2. Profile_AppVersionChangeInvalidatesIncompatibleSelector
    // =========================================================================
    [Fact]
    public void Profile_AppVersionChangeInvalidatesIncompatibleSelector()
    {
        var identity = new ApplicationIdentity(
            canonicalExecutablePath: "C:\\Program Files\\Calculator\\calc.exe",
            displayName: "Calculator");

        var store = new ApplicationProfileStore(_tempDir);
        var profile = new ApplicationProfile
        {
            Identity = identity,
            AppVersion = "1.0.0"
        };

        // Selector tied specifically to version 1.0.0
        var v1Landmark = new LandmarkKnowledge
        {
            Name = "LegacyResultDisplay",
            Selector = new LandmarkSelector { Role = "Text", AutomationId = "ResultOld" },
            TargetAppVersion = "1.0.0",
            Validated = true
        };

        // Version-agnostic selector
        var agnosticLandmark = new LandmarkKnowledge
        {
            Name = "ClearButton",
            Selector = new LandmarkSelector { Role = "Button", AutomationId = "Clear" },
            TargetAppVersion = null,
            Validated = true
        };

        profile.AddOrUpdateLandmark(v1Landmark);
        profile.AddOrUpdateLandmark(agnosticLandmark);
        profile.Hints = new LaunchHints { StartupDelayMs = 150 };
        profile.AddRecipe(new TaskRecipeDefinition("ClearCalc", "Clears calculator", "calc", Array.Empty<RecipeParameter>(), Array.Empty<string>(), "[]", Array.Empty<string>(), DateTimeOffset.UtcNow));

        store.SaveProfile(profile);

        // App updates to 2.0.0: loading profile with new version invalidates incompatible selectors
        var updatedProfile = store.GetProfile(identity, currentAppVersion: "2.0.0");

        Assert.NotNull(updatedProfile);
        Assert.Equal("2.0.0", updatedProfile.AppVersion);

        // Incompatible selector is evicted
        Assert.False(updatedProfile.Landmarks.ContainsKey("LegacyResultDisplay"));

        // Version-agnostic selector is preserved
        Assert.True(updatedProfile.Landmarks.ContainsKey("ClearButton"));

        // Version-agnostic metadata (hints, recipes) is preserved
        Assert.NotNull(updatedProfile.Hints);
        Assert.Equal(150, updatedProfile.Hints.StartupDelayMs);
        Assert.Single(updatedProfile.Recipes);
        Assert.Equal("ClearCalc", updatedProfile.Recipes[0].Name);
    }

    // =========================================================================
    // 3. Profile_SelectorFailureEvictsOnlyAffectedLandmark
    // =========================================================================
    [Fact]
    public async Task Profile_SelectorFailureEvictsOnlyAffectedLandmark()
    {
        var identity = new ApplicationIdentity(canonicalExecutablePath: "C:\\Tools\\App.exe", displayName: "App");
        var store = new ApplicationProfileStore(_tempDir);
        var profile = new ApplicationProfile { Identity = identity, AppVersion = "1.0.0" };

        var lm1 = new LandmarkKnowledge
        {
            Name = "SearchBox",
            Selector = new LandmarkSelector { Role = "Edit", AutomationId = "Search" },
            Validated = true
        };
        var lm2 = new LandmarkKnowledge
        {
            Name = "StatusBar",
            Selector = new LandmarkSelector { Role = "StatusBar", AutomationId = "Status" },
            Validated = true
        };

        profile.AddOrUpdateLandmark(lm1);
        profile.AddOrUpdateLandmark(lm2);
        store.SaveProfile(profile);

        var healer = new LandmarkSelfHealer(store);

        // Attempt resolving "SearchBox": resolution fails, live rediscovery finds nothing
        var outcome = await healer.ResolveOrHealAsync(
            profile,
            "SearchBox",
            resolver: (_, _) => Task.FromResult<UiElement?>(null), // fails
            rediscovery: (_, _) => Task.FromResult<LandmarkKnowledge?>(null), // rediscovery returns nothing
            validator: (_, _, _) => Task.FromResult(false));

        Assert.False(outcome.Success);
        Assert.True(outcome.Evicted);

        // Verify: SearchBox was evicted, but StatusBar was NOT touched!
        Assert.False(profile.Landmarks.ContainsKey("SearchBox"));
        Assert.True(profile.Landmarks.ContainsKey("StatusBar"));

        // Verify persistence: reloaded profile also reflects that ONLY SearchBox was evicted
        var reloaded = store.GetProfile(identity);
        Assert.NotNull(reloaded);
        Assert.False(reloaded.Landmarks.ContainsKey("SearchBox"));
        Assert.True(reloaded.Landmarks.ContainsKey("StatusBar"));
    }

    // =========================================================================
    // 4. Profile_LiveUiStateIsNeverPersisted
    // =========================================================================
    [Fact]
    public void Profile_LiveUiStateIsNeverPersisted()
    {
        // 1. Prohibit PID or HWND as ApplicationIdentity
        Assert.Throws<ArgumentException>(() => new ApplicationIdentity(appId: "0x00040A2C"));
        Assert.Throws<ArgumentException>(() => new ApplicationIdentity(canonicalExecutablePath: "pid:8124"));
        Assert.Throws<ArgumentException>(() => new ApplicationIdentity(canonicalExecutablePath: "12345"));

        // 2. Prohibit transient handles / live IDs in LandmarkSelector
        var transientSelector = new LandmarkSelector { AutomationId = "uia_5_12" };
        Assert.Throws<InvalidOperationException>(() => transientSelector.AssertNoLiveUiState());

        var hwndSelector = new LandmarkSelector { Name = "hwnd:0x1234" };
        Assert.Throws<InvalidOperationException>(() => hwndSelector.AssertNoLiveUiState());

        // 3. Structural reflection check: ApplicationProfile schema must contain ZERO live OS handle types
        var profileProps = typeof(ApplicationProfile).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (var prop in profileProps)
        {
            Assert.NotEqual(typeof(IntPtr), prop.PropertyType);
            Assert.NotEqual(typeof(UIntPtr), prop.PropertyType);
            Assert.NotEqual(typeof(nint), prop.PropertyType);
            Assert.NotEqual(typeof(nuint), prop.PropertyType);
            Assert.DoesNotContain("hwnd", prop.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("pid", prop.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("bounds", prop.Name, StringComparison.OrdinalIgnoreCase);
        }

        // 4. Serialized JSON format check: zero HWND or live bounds emitted
        var store = new ApplicationProfileStore(_tempDir);
        var id = new ApplicationIdentity(canonicalExecutablePath: "C:\\App\\app.exe", displayName: "App");
        var profile = new ApplicationProfile { Identity = id };
        profile.AddOrUpdateLandmark(new LandmarkKnowledge
        {
            Name = "Main",
            Selector = new LandmarkSelector { Role = "Window", AutomationId = "MainWindow" }
        });
        store.SaveProfile(profile);

        var json = File.ReadAllText(store.GetProfileFilePath(id));
        Assert.DoesNotContain("\"Hwnd\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"Pid\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"Bounds\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"ComObject\"", json, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // 5. Profile_ConcurrentWritersDoNotCorruptKnowledge
    // =========================================================================
    [Fact]
    public void Profile_ConcurrentWritersDoNotCorruptKnowledge()
    {
        var store = new ApplicationProfileStore(_tempDir);
        var identity = new ApplicationIdentity(canonicalExecutablePath: "C:\\Apps\\SharedApp.exe", displayName: "SharedApp");

        const int writerCount = 15;
        var exceptions = new ConcurrentBag<Exception>();

        Parallel.For(0, writerCount, i =>
        {
            try
            {
                // Each writer creates/updates profile
                var prof = store.GetProfile(identity) ?? new ApplicationProfile { Identity = identity, AppVersion = "1.0.0" };
                prof.AddOrUpdateLandmark(new LandmarkKnowledge
                {
                    Name = $"Landmark_{i}",
                    Selector = new LandmarkSelector { Role = "Button", AutomationId = $"Btn_{i}" },
                    Validated = true
                });

                store.SaveProfile(prof);
            }
            catch (StaleProfileException)
            {
                // Stale writer rejection is expected under high concurrency and is valid
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        // No unhandled file corruption exceptions
        Assert.Empty(exceptions);

        // Verify on-disk file is perfectly valid, parseable JSON
        var path = store.GetProfileFilePath(identity);
        Assert.True(File.Exists(path));
        var content = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(content);
        Assert.Equal(ApplicationProfile.CurrentFormatVersion, doc.RootElement.GetProperty("FormatVersion").GetInt32());
    }

    // =========================================================================
    // 6. Profile_StaleWriterCannotOverwriteNewerValidatedKnowledge
    // =========================================================================
    [Fact]
    public void Profile_StaleWriterCannotOverwriteNewerValidatedKnowledge()
    {
        var store = new ApplicationProfileStore(_tempDir);
        var identity = new ApplicationIdentity(canonicalExecutablePath: "C:\\Apps\\Editor.exe", displayName: "Editor");

        // Initial profile at Revision 1
        var initial = new ApplicationProfile
        {
            Identity = identity,
            AppVersion = "1.0.0",
            Revision = 1
        };
        store.SaveProfile(initial);

        // Writer A loads Revision 1
        var writerAProfile = store.GetProfile(identity)!;
        Assert.Equal(1, writerAProfile.Revision);

        // Writer B loads Revision 1, adds newly validated knowledge, and saves -> commits Revision 2
        var writerBProfile = store.GetProfile(identity)!;
        writerBProfile.AddOrUpdateLandmark(new LandmarkKnowledge
        {
            Name = "Validated_Toolbar",
            Selector = new LandmarkSelector { Role = "Toolbar", AutomationId = "MainBar" },
            Validated = true
        });
        store.SaveProfile(writerBProfile);

        var onDiskAfterB = store.GetProfile(identity)!;
        Assert.True(onDiskAfterB.Revision >= 2);
        Assert.True(onDiskAfterB.Landmarks.ContainsKey("Validated_Toolbar"));

        // Writer A (holding stale Revision 1) tries to commit older knowledge
        writerAProfile.AddOrUpdateLandmark(new LandmarkKnowledge
        {
            Name = "Stale_OldLandmark",
            Selector = new LandmarkSelector { Role = "Edit", AutomationId = "OldEdit" }
        });

        // Must reject stale overwrite!
        Assert.Throws<StaleProfileException>(() => store.SaveProfile(writerAProfile));

        // Verify on disk: Writer B's validated knowledge was PRESERVED!
        var finalProfile = store.GetProfile(identity)!;
        Assert.True(finalProfile.Landmarks.ContainsKey("Validated_Toolbar"));
        Assert.False(finalProfile.Landmarks.ContainsKey("Stale_OldLandmark"));
    }

    // =========================================================================
    // 7. Profile_RecipeUsesExistingRecipeSystem
    // =========================================================================
    [Fact]
    public void Profile_RecipeUsesExistingRecipeSystem()
    {
        var recipeStore = new InMemoryTaskRecipeStore();
        var store = new ApplicationProfileStore(_tempDir, recipeStore: recipeStore);
        var identity = new ApplicationIdentity(canonicalExecutablePath: "C:\\Apps\\Notes.exe", displayName: "Notes");

        var profile = new ApplicationProfile { Identity = identity, AppVersion = "1.0.0" };

        var recipe = new TaskRecipeDefinition(
            Name: "ExportNoteAsMarkdown",
            Description: "Exports active note as markdown",
            App: "notes",
            Parameters: new[] { new RecipeParameter("FileName", "Destination file path", "note.md") },
            Preconditions: new[] { "EditorReady" },
            StepsJson: "[{\"action\":\"hotkey\",\"keys\":\"ctrl+shift+s\"}]",
            Postconditions: new[] { "FileSaved" },
            CreatedAt: DateTimeOffset.UtcNow);

        // Add recipe to profile and save
        profile.AddRecipe(recipe, recipeStore);
        store.SaveProfile(profile);

        // Verify: Recipe exists in profile
        var loadedProfile = store.GetProfile(identity)!;
        Assert.Single(loadedProfile.Recipes);
        Assert.Equal("ExportNoteAsMarkdown", loadedProfile.Recipes[0].Name);

        // Verify: Recipe is seamlessly synced and retrievable via the existing recipe system
        var retrievedFromRecipeSystem = recipeStore.Get("ExportNoteAsMarkdown");
        Assert.NotNull(retrievedFromRecipeSystem);
        Assert.Equal("ExportNoteAsMarkdown", retrievedFromRecipeSystem.Name);
        Assert.Equal("notes", retrievedFromRecipeSystem.App);
        Assert.Single(retrievedFromRecipeSystem.Parameters);
        Assert.Equal("FileName", retrievedFromRecipeSystem.Parameters[0].Name);
    }

    // =========================================================================
    // 8. Profile_DeepLinkCannotBecomeArbitraryCommandExecution
    // =========================================================================
    [Fact]
    public void Profile_DeepLinkCannotBecomeArbitraryCommandExecution()
    {
        var profile = new ApplicationProfile
        {
            Identity = new ApplicationIdentity(displayName: "TestApp", canonicalExecutablePath: "C:\\App\\app.exe")
        };

        // Dangerous execution vectors must be rejected
        Assert.Throws<SecurityException>(() => profile.AddDeepLink("cmd.exe /c calc.exe"));
        Assert.Throws<SecurityException>(() => profile.AddDeepLink("powershell.exe -enc dGVzdA=="));
        Assert.Throws<SecurityException>(() => profile.AddDeepLink("file:///C:/Windows/System32/cmd.exe"));
        Assert.Throws<SecurityException>(() => profile.AddDeepLink("ms-msdt:/id PCWDiagnostic"));
        Assert.Throws<SecurityException>(() => profile.AddDeepLink("spotify:track:123 & calc.exe"));
        Assert.Throws<SecurityException>(() => profile.AddDeepLink("calc:run.bat"));

        // Safe registered application protocols must be accepted
        profile.AddDeepLink("spotify:track:6rqhFgbbKwnb9MLmUQDhG6");
        profile.AddDeepLink("vscode://file/c:/workspace/app.cs");
        profile.AddDeepLink("slack://channel?id=C12345");

        Assert.Equal(3, profile.DeepLinks.Count);
        Assert.Contains("spotify:track:6rqhFgbbKwnb9MLmUQDhG6", profile.DeepLinks);
        Assert.Contains("vscode://file/c:/workspace/app.cs", profile.DeepLinks);
        Assert.Contains("slack://channel?id=C12345", profile.DeepLinks);
    }

    // =========================================================================
    // 9. Launch_AppMapUsesProfileKnowledge
    // =========================================================================
    [Fact]
    public async Task Launch_AppMapUsesProfileKnowledge()
    {
        var store = new ApplicationProfileStore(_tempDir);
        var identity = new ApplicationIdentity(displayName: "SuperEditor", canonicalExecutablePath: "C:\\Editor\\editor.exe");

        var profile = new ApplicationProfile { Identity = identity, AppVersion = "3.2.0" };
        profile.AddOrUpdateLandmark(new LandmarkKnowledge
        {
            Name = "SideNavigationPanel",
            Selector = new LandmarkSelector { Role = "Tree", AutomationId = "NavTree" },
            Validated = true
        });
        profile.AddOrUpdateLandmark(new LandmarkKnowledge
        {
            Name = "CodeInspectorPane",
            Selector = new LandmarkSelector { Role = "Pane", AutomationId = "Inspector" },
            Validated = true
        });
        store.SaveProfile(profile);

        var appService = new FakeAppService
        {
            OnLaunch = spec => new LaunchResult(
                Success: true,
                Method: LaunchMethod.Executable,
                ResolvedName: "SuperEditor",
                ResolvedIdentifier: "C:\\Editor\\editor.exe",
                Pid: 5432,
                Hwnd: 0x4002,
                WindowTitle: "SuperEditor Professional",
                LaunchState: "Ready",
                LaunchMs: 20,
                ReadyMs: 30,
                Error: null)
        };
        var winService = new FakeWindowService();

        var executor = new LaunchContinuationExecutor(appService, winService, store);

        var result = await executor.ExecuteLaunchWithContinuationAsync(
            new LaunchSpec(App: "SuperEditor"),
            explicitProfile: profile);

        Assert.True(result.Success);
        Assert.NotNull(result.InitialMap);

        var landmarksObj = result.InitialMap["landmarks"] as IEnumerable<string>;
        Assert.NotNull(landmarksObj);
        var landmarksList = landmarksObj.ToList();

        // Verify: App map contains landmarks sourced from the persistent profile knowledge
        Assert.Contains("SideNavigationPanel", landmarksList);
        Assert.Contains("CodeInspectorPane", landmarksList);
    }

    // =========================================================================
    // 10. LaunchThen_ReusesCanonicalPlanExecutor
    // =========================================================================
    [Fact]
    public async Task LaunchThen_ReusesCanonicalPlanExecutor()
    {
        var appService = new FakeAppService();
        var winService = new FakeWindowService();

        bool canonicalExecutorCalled = false;
        IReadOnlyList<object>? executedSteps = null;

        Func<IReadOnlyList<object>, CancellationToken, Task<object?>> canonicalPlanExecutor = (steps, ct) =>
        {
            canonicalExecutorCalled = true;
            executedSteps = steps;
            return Task.FromResult<object?>(new Dictionary<string, object?>
            {
                ["status"] = "Ok",
                ["stepsExecuted"] = steps.Count,
                ["total"] = steps.Count
            });
        };

        var executor = new LaunchContinuationExecutor(
            appService,
            winService,
            planExecutor: canonicalPlanExecutor);

        var step1 = new { action = "click", target = "SubmitBtn" };
        var step2 = new { action = "wait", ms = 100 };
        var continuationSteps = new object[] { step1, step2 };

        var result = await executor.ExecuteLaunchWithContinuationAsync(
            new LaunchSpec(App: "TestApp"),
            thenSteps: continuationSteps);

        Assert.True(result.Success);
        Assert.Equal("Verified", result.Status);
        Assert.True(canonicalExecutorCalled, "Canonical plan executor was not reused for continuation steps!");
        Assert.NotNull(executedSteps);
        Assert.Equal(2, executedSteps.Count);
        Assert.NotNull(result.ContinuationOutcome);
    }

    // =========================================================================
    // 11. LaunchThen_UnexpectedModalReturnsSingleMeaningfulPause
    // =========================================================================
    [Fact]
    public async Task LaunchThen_UnexpectedModalReturnsSingleMeaningfulPause()
    {
        var appService = new FakeAppService();
        var winService = new FakeWindowService
        {
            // Simulate an unexpected blocking modal dialog on top of the launched app
            ModalToReturn = new WindowInfo(
                Hwnd: 0x9999,
                Pid: 1234,
                Title: "Do you want to save unsaved changes?",
                ProcessName: "testapp",
                Bounds: new RectPx(100, 100, 300, 200),
                State: WindowState.Normal,
                IsForeground: true,
                IsElevated: false,
                OnCurrentVirtualDesktop: true,
                MonitorIndex: 0,
                IsModalPopup: true,
                IsEnabled: true)
        };

        bool continuationExecuted = false;
        Func<IReadOnlyList<object>, CancellationToken, Task<object?>> planExecutor = (steps, ct) =>
        {
            continuationExecuted = true;
            return Task.FromResult<object?>("executed");
        };

        var executor = new LaunchContinuationExecutor(
            appService,
            winService,
            planExecutor: planExecutor);

        var thenSteps = new object[] { new { action = "click", target = "Save" } };

        var result = await executor.ExecuteLaunchWithContinuationAsync(
            new LaunchSpec(App: "TestApp"),
            thenSteps: thenSteps);

        // Must NOT fail catastrophically and must NOT execute continuation on blocked window
        Assert.False(continuationExecuted, "Continuation should not execute while window is blocked by unexpected modal!");
        Assert.True(result.IsPaused);
        Assert.Equal("Paused", result.Status);
        Assert.Equal("UnexpectedModal", result.PauseReason);
        Assert.NotNull(result.Modal);
        Assert.Contains("unexpected modal", result.Note, StringComparison.OrdinalIgnoreCase);
    }
}
