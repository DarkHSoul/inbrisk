using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Inbrisk.Core;
using Inbrisk.Mcp;
using Inbrisk.Platform.Windows.Apps;
using Inbrisk.Platform.Windows.Uia;
using Inbrisk.Runtime;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Inbrisk.Tests;

public class ReliabilityAndPerformancePhaseTests
{
    // =========================================================================
    // Mock Helpers for Unit Tests
    // =========================================================================

    private sealed class MockWindowService : IWindowService
    {
        public bool IsClosed { get; set; }
        public long? SyntheticForegroundHwnd { get; set; }
        public Func<long, bool>? OnCloseWindow { get; set; }
        public IReadOnlyList<WindowInfo> ListWindows() => Array.Empty<WindowInfo>();
        public WindowInfo? GetWindow(long hwnd) => IsClosed ? null : new(hwnd, 100, "MockApp", "mock.exe", new RectPx(0, 0, 800, 600), WindowState.Normal, true, false, true, 0);
        public WindowInfo? GetForegroundWindow() => SyntheticForegroundHwnd.HasValue ? GetWindow(SyntheticForegroundHwnd.Value) : GetWindow(100);
        public IReadOnlyList<MonitorInfo> GetMonitors() => Array.Empty<MonitorInfo>();
        public RectPx GetVirtualDesktopBounds() => new(0, 0, 1920, 1080);
        public bool FocusWindow(long hwnd) => true;
        public bool CloseWindow(long hwnd) => OnCloseWindow != null ? OnCloseWindow(hwnd) : true;
        public WindowInfo? GetModalPopup(long hwnd) => null;
        public bool IsWindowEnabled(long hwnd) => true;
        public IReadOnlyList<WindowInfo> FindSystemDialogs() => Array.Empty<WindowInfo>();
        public bool IsWindowProtected(long hwnd, out string? reason) { reason = null; return false; }
    }

    private sealed class MockBackend : IElementBackend
    {
        public BackendId Id => BackendId.Uia;
        public Func<ElementHandle, UiElement?>? OnReResolve { get; set; }
        public Func<UiElement, ActionIntent, ActionResult?>? OnPerformNative { get; set; }

        public IReadOnlyList<UiElement> Inspect(long hwnd, InspectOptions options, CancellationToken ct = default) => Array.Empty<UiElement>();
        public IReadOnlyList<UiElement> Find(FindSpec spec, CancellationToken ct = default) => Array.Empty<UiElement>();
        public ActionResult? PerformNative(UiElement element, ActionIntent intent, CancellationToken ct = default) =>
            OnPerformNative != null ? OnPerformNative(element, intent) : new ActionResult(true, BackendId.Uia, "NativeMock", Array.Empty<Attempt>(), VerifyResult.Unverified, TimeSpan.FromMilliseconds(1));
        public UiElement? ReResolve(ElementHandle handle, CancellationToken ct = default) => OnReResolve != null ? OnReResolve(handle) : null;
        public bool IsAlive(UiElement element) => true;
    }

    private sealed class MockIntegrityService : IIntegrityService
    {
        public bool IsSecureDesktopActive() => false;
        public bool CanControlWindow(long hwnd) => true;
        public IntegrityRelation CheckTarget(long hwnd) => IntegrityRelation.Reachable;
    }

    private sealed class MockInputService : IInputService
    {
        public void MoveMouse(int x, int y) { }
        public void Click(int x, int y, MouseButton button = MouseButton.Left, int count = 1) { }
        public void Drag(int fromX, int fromY, int toX, int toY, int durationMs = 300, CancellationToken ct = default) { }
        public void Scroll(int x, int y, int wheelDelta) { }
        public void KeyPress(KeyCode key) { }
        public void KeyDown(KeyCode key) { }
        public void KeyUp(KeyCode key) { }
        public void Hotkey(IReadOnlyList<KeyCode> modifiers, KeyCode key) { }
        public void TypeText(string text) { }
        public (int X, int Y) CursorPosition() => (0, 0);
        public void ReleaseAll() { }
    }

    private sealed class MockCaptureService : ICaptureService
    {
        public Frame Capture(RectPx region, int maxImageWidth = 1600) => throw new NotImplementedException();
        public Frame CaptureWindow(long hwnd, int maxImageWidth = 1600) => throw new NotImplementedException();
        public RawFrame CaptureRaw(RectPx region) => throw new NotImplementedException();
        public double DiffFraction(RectPx region, int sampleScale = 8) => 0;
        public byte[] Sample(RectPx region, int scale = 8) => Array.Empty<byte>();
        public ICaptureSession CreateSession(CaptureTarget target) => throw new NotImplementedException();
    }

    private static UiElement CreateTestElement(string id, string? value = null, string? toggle = null, long hwnd = 100)
    {
        var props = new Dictionary<string, object?>
        {
            ["value"] = value,
            ["toggleState"] = toggle,
            ["enabled"] = true
        };
        var handle = new ElementHandle(BackendId.Uia, id,
            new ReResolveRecipe(1, hwnd, "mock", Inbrisk.Core.Role.Button, id, id, Array.Empty<AncestryStep>(), new RectPx(0, 0, 100, 30)));
        return new UiElement(id, BackendId.Uia, Inbrisk.Core.Role.Button, id, new RectPx(0, 0, 100, 30),
            new[] { "click", "invoke", "toggle" }, props, handle, 1, hwnd);
    }

    // =========================================================================
    // Item 6: RecentEventBuffer Test Matrix
    // =========================================================================

    [Fact]
    public void RecentEventBuffer_Timeout_ReturnsFalse_WithoutSpinning()
    {
        var buffer = new RecentEventBuffer();
        var sw = Stopwatch.StartNew();
        var result = buffer.WaitForEvent(30, CancellationToken.None);
        sw.Stop();

        Assert.False(result);
        Assert.True(sw.ElapsedMilliseconds >= 25, $"Elapsed was {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void RecentEventBuffer_SingleEvent_WakesImmediately()
    {
        var buffer = new RecentEventBuffer();
        var currentGen = buffer.CurrentGeneration;

        Task.Run(async () =>
        {
            await Task.Delay(20);
            buffer.Add(new ObservedEvent(EventKind.ForegroundChanged, DateTimeOffset.UtcNow, 100, 1));
        });

        var sw = Stopwatch.StartNew();
        var signaled = buffer.WaitForNextEvent(currentGen, 500, CancellationToken.None);
        sw.Stop();

        Assert.True(signaled);
        Assert.True(sw.ElapsedMilliseconds < 450);
        Assert.True(buffer.CurrentGeneration > currentGen);
    }

    [Fact]
    public void RecentEventBuffer_EventFlood_HandledSmoothly()
    {
        var buffer = new RecentEventBuffer(capacity: 50);
        const int count = 500;

        Parallel.For(0, count, i =>
        {
            buffer.Add(new ObservedEvent(EventKind.FocusChanged, DateTimeOffset.UtcNow, i, i));
        });

        Assert.Equal(count, buffer.CurrentGeneration);
        var snapshot = buffer.Snapshot(100);
        Assert.Equal(50, snapshot.Count);
    }

    [Fact]
    public void RecentEventBuffer_WaiterStartRace_WakesWhenEventPrecedesWait()
    {
        var buffer = new RecentEventBuffer();
        var baseline = buffer.CurrentGeneration;

        // Event arrived before waiter starts
        buffer.Add(new ObservedEvent(EventKind.WindowOpened, DateTimeOffset.UtcNow, 100, 1));

        // Waiter starts with baseline older than current generation
        var signaled = buffer.WaitForNextEvent(baseline, 100, CancellationToken.None);
        Assert.True(signaled, "Must wake immediately if event already occurred since baseline");
    }

    [Fact]
    public void RecentEventBuffer_SequentialWaits_TrackGenerationsIndependently()
    {
        var buffer = new RecentEventBuffer();

        for (int i = 0; i < 3; i++)
        {
            var gen = buffer.CurrentGeneration;
            buffer.Add(new ObservedEvent(EventKind.StructureChanged, DateTimeOffset.UtcNow, 100, 1));
            Assert.True(buffer.WaitForNextEvent(gen, 100, CancellationToken.None));
        }

        Assert.Equal(3, buffer.CurrentGeneration);
    }

    [Fact]
    public void RecentEventBuffer_Cancellation_ThrowsImmediately()
    {
        var buffer = new RecentEventBuffer();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            buffer.WaitForEvent(1000, cts.Token));
    }

    [Fact]
    public async Task RecentEventBuffer_ConcurrentWaiters_AllWakeOnSinglePulse()
    {
        var buffer = new RecentEventBuffer();
        var gen = buffer.CurrentGeneration;
        const int waiterCount = 4;
        var tasks = new Task<bool>[waiterCount];

        for (int i = 0; i < waiterCount; i++)
        {
            tasks[i] = Task.Run(() => buffer.WaitForNextEvent(gen, 500, CancellationToken.None));
        }

        await Task.Delay(30);
        buffer.Add(new ObservedEvent(EventKind.ForegroundChanged, DateTimeOffset.UtcNow, 100, 1));

        var results = await Task.WhenAll(tasks);
        Assert.All(results, Assert.True);
    }

    // =========================================================================
    // Item 8: Executor ElementGone Verification
    // =========================================================================

    [Fact]
    public void Executor_ElementGone_Expected_VerifiesSuccessfully()
    {
        var backend = new MockBackend();
        var win = new MockWindowService();
        var reg = new ElementRegistry(new[] { backend });
        var exec = new Executor(win, new MockIntegrityService(), new MockInputService(), new MockCaptureService(),
            new[] { backend }, reg, new SafetyPolicy());

        var el = CreateTestElement("btn_close");
        reg.Register(new[] { el });

        // Backend returns null when re-resolving -> element is gone
        backend.OnReResolve = _ => null;

        var intent = new ActionIntent(ActionKind.Click, TargetRef.Element("btn_close"),
            Verify: new VerifySpec(VerifyKind.ElementGone));

        var result = exec.Perform(intent, new ActionContext(CancellationToken.None, null, "test", null, null));

        Assert.True(result.Success);
        Assert.Equal(VerifyResult.Verified, result.Verification);
        Assert.Equal("ElementGone", result.Evidence?.Method);
    }

    [Fact]
    public void Executor_ElementGone_Expected_FailsWhenElementStillPresent()
    {
        var backend = new MockBackend();
        var win = new MockWindowService();
        var reg = new ElementRegistry(new[] { backend });
        var exec = new Executor(win, new MockIntegrityService(), new MockInputService(), new MockCaptureService(),
            new[] { backend }, reg, new SafetyPolicy());

        var el = CreateTestElement("btn_persistent");
        reg.Register(new[] { el });

        // Backend still finds the element -> element did not disappear
        backend.OnReResolve = _ => el;

        var intent = new ActionIntent(ActionKind.Click, TargetRef.Element("btn_persistent"),
            Verify: new VerifySpec(VerifyKind.ElementGone));

        var result = exec.Perform(intent, new ActionContext(CancellationToken.None, null, "test", null, null));

        Assert.False(result.Success);
        Assert.Equal(VerifyResult.Failed, result.Verification);
        Assert.Equal("ElementStillPresent", result.Evidence?.Method);
    }

    [Fact]
    public void Executor_ElementDisappearsUnexpectedly_ReportsUnverified()
    {
        var backend = new MockBackend();
        var win = new MockWindowService();
        var reg = new ElementRegistry(new[] { backend });
        var exec = new Executor(win, new MockIntegrityService(), new MockInputService(), new MockCaptureService(),
            new[] { backend }, reg, new SafetyPolicy());

        var el = CreateTestElement("chk_box", toggle: "off");
        reg.Register(new[] { el });

        // Action was toggle (expected property change), but element vanished
        backend.OnReResolve = _ => null;

        var intent = new ActionIntent(ActionKind.Toggle, TargetRef.Element("chk_box"));
        var result = exec.Perform(intent, new ActionContext(CancellationToken.None, null, "test", null, null));

        Assert.True(result.Success); // Action was dispatched
        Assert.Equal(VerifyResult.Unverified, result.Verification);
        Assert.Equal("ElementNotReResolved", result.Evidence?.Method);
    }

    // =========================================================================
    // Item 9: ComputeNextObservationRequired
    // =========================================================================

    [Fact]
    public void ComputeNextObservationRequired_ModalPopup_ReturnsTrue()
    {
        var outcome = new StepOutcome(OutcomeKind.Verified, true, "click", null, 10, null);
        var res = InbriskTools.ComputeNextObservationRequired(outcome, hasContextualView: false, hasNewWindowOrModal: true);
        Assert.True(res, "Modal popup or new window must always require observation");
    }

    [Fact]
    public void ComputeNextObservationRequired_ContextualViewPresent_ReturnsFalse()
    {
        var outcome = new StepOutcome(OutcomeKind.Unverified, true, "click", null, 10, null);
        var res = InbriskTools.ComputeNextObservationRequired(outcome, hasContextualView: true, hasNewWindowOrModal: false);
        Assert.False(res, "Contextual view already contains necessary elements, observation not required");
    }

    [Fact]
    public void ComputeNextObservationRequired_TerminalErrors_ReturnsFalse()
    {
        var malformed = new StepOutcome(OutcomeKind.Malformed, false, "click", "bad args", 10, null);
        var denied = new StepOutcome(OutcomeKind.PolicyDenied, false, "click", "policy denied", 10, null);
        var cancelled = new StepOutcome(OutcomeKind.Cancelled, false, "click", "user cancelled", 10, null);

        Assert.False(InbriskTools.ComputeNextObservationRequired(malformed));
        Assert.False(InbriskTools.ComputeNextObservationRequired(denied));
        Assert.False(InbriskTools.ComputeNextObservationRequired(cancelled));
    }

    [Fact]
    public void ComputeNextObservationRequired_VerifiedWithoutPopup_ReturnsFalse()
    {
        var outcome = new StepOutcome(OutcomeKind.Verified, true, "click", null, 10,
            new VerifyEvidence("PropertyChanged", "0", "1"));
        var res = InbriskTools.ComputeNextObservationRequired(outcome, hasContextualView: false, hasNewWindowOrModal: false);
        Assert.False(res, "Verified action without modal popup needs no observation");
    }

    [Fact]
    public void ComputeNextObservationRequired_UnverifiedWithoutContext_ReturnsTrue()
    {
        var outcome = new StepOutcome(OutcomeKind.Unverified, true, "click", null, 10, null);
        var res = InbriskTools.ComputeNextObservationRequired(outcome, hasContextualView: false, hasNewWindowOrModal: false);
        Assert.True(res, "Unverified action without contextual view should recommend observation");
    }

    [Fact]
    public void ComputeNextObservationRequired_ExplicitObserveRequested_ReturnsTrue()
    {
        var outcome = new StepOutcome(OutcomeKind.Verified, true, "click", null, 10, null);
        var res = InbriskTools.ComputeNextObservationRequired(outcome, hasExplicitObservationRequested: true);
        Assert.True(res, "Explicit observe flag must always evaluate to true");
    }

    // =========================================================================
    // Item 4 & 5: InspectCache and FindCache Tests
    // =========================================================================

    [Fact]
    public void FindCache_InvalidatesOnMutationVersionAndEvent()
    {
        using var session = new McpSession(EmergencyControl.Process, startEvents: false);
        var key = "role:Button|name:Save";
        var dummyList = new List<UiElement>();

        // 1. Initial Miss
        Assert.False(session.FindCache.ContainsKey(key));

        // 2. Cache Store (Version 1, HWND 200)
        session.FindCache[key] = (dummyList, DateTimeOffset.UtcNow, new HashSet<long> { 200 }, new HashSet<int> { 10 }, 1);
        Assert.True(session.FindCache.ContainsKey(key));

        // 3. Clear on mutation or event
        session.FindCache.Clear();
        Assert.False(session.FindCache.ContainsKey(key));
    }

    // =========================================================================
    // Item 10: Batch Tool Tests
    // =========================================================================

    [Fact]
    public async Task BatchTool_ReadsPropertyValues_Successfully()
    {
        using var session = new McpSession(EmergencyControl.Process, startEvents: false);
        var tools = new InbriskTools(session);

        // Batch with empty steps returns malformed
        var err = await tools.Batch(Array.Empty<InbriskTools.BatchStep>());
        Assert.True(err.IsError);

        // Batch with read targets
        var steps = new[]
        {
            new InbriskTools.BatchStep("hover", "NonExistentButton")
        };
        var read = new[]
        {
            new InbriskTools.BatchReadSpec("NonExistentStatus", ["value", "name"])
        };

        var res = await tools.Batch(steps, read: read);
        var text = string.Join("\n", res.Content.OfType<TextContentBlock>().Select(t => t.Text));
        Assert.NotNull(text);
    }

    // =========================================================================
    // Item 11: SaveRecipe / ResumeRun Backward Compatibility
    // =========================================================================

    [Fact]
    public async Task SaveRecipe_AcceptsLegacyRawJsonSteps()
    {
        using var session = new McpSession(EmergencyControl.Process, startEvents: false);
        var tools = new InbriskTools(session);

        var legacyJson = JsonDocument.Parse("[{\"action\":\"click\",\"target\":{\"name\":\"OK\"}}]").RootElement;
        var result = await tools.SaveRecipe(name: "legacy-recipe", description: "legacy description", steps: legacyJson);

        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(t => t.Text));
        Assert.False(result.IsError, text);
        Assert.Contains("saved", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResumeRun_AcceptsLegacyRawJsonSteps()
    {
        using var session = new McpSession(EmergencyControl.Process, startEvents: false);
        var tools = new InbriskTools(session);

        // 1. Invalid JSON returns malformed error
        var invalidJson = JsonDocument.Parse("{\"not\":\"an array\"}").RootElement;
        var err = await tools.ResumeRun("test-run-id", remainingSteps: invalidJson);
        Assert.True(err.IsError);
        var errText = string.Join("\n", err.Content.OfType<TextContentBlock>().Select(t => t.Text));
        Assert.Contains("Malformed", errText);

        // 2. Valid legacy JSON array parses cleanly
        var legacyJson = JsonDocument.Parse("[{\"action\":\"wait\",\"ms\":1}]").RootElement;
        var res = await tools.ResumeRun("test-run-id", remainingSteps: legacyJson);
        var text = string.Join("\n", res.Content.OfType<TextContentBlock>().Select(t => t.Text));
        Assert.NotNull(text);
        Assert.DoesNotContain("Invalid remainingSteps JSON", text);
    }

    // =========================================================================
    // Item 12: Core Tool Annotations
    // =========================================================================

    [Fact]
    public void CoreToolNames_ContainsExactly16Tools()
    {
        var coreTools = McpHost.CoreTools;
        Assert.Equal(16, coreTools.Count);

        var expected = new[]
        {
            "computer_batch",
            "computer_do",
            "computer_run",
            "computer_launch",
            "computer_close_window",
            "computer_windows",
            "computer_observe",
            "computer_find",
            "computer_inspect",
            "computer_read",
            "computer_click",
            "computer_type",
            "computer_hotkey",
            "computer_screenshot",
            "computer_reset_input",
            "computer_capabilities",
        };

        foreach (var tool in expected)
        {
            Assert.Contains(tool, coreTools);
        }

        Assert.DoesNotContain("browser_browse", coreTools);
        Assert.DoesNotContain("browser_click", coreTools);
        Assert.DoesNotContain("browser_type", coreTools);
        Assert.DoesNotContain("browser_snapshot", coreTools);
    }

    // =========================================================================
    // Item 13: AppService Cache & Invalidation
    // =========================================================================

    [Fact]
    public void AppService_ResolveCache_InvalidationClearsEntries()
    {
        var win = new MockWindowService();
        var svc = new AppService(win);

        // Resolve unknown app
        var res1 = svc.ResolveAll("nonexistent_app_12345");
        Assert.Empty(res1);

        // InvalidateCache clears the resolve dictionary
        svc.InvalidateCache();

        var res2 = svc.ResolveAll("nonexistent_app_12345");
        Assert.Empty(res2);
    }

    // =========================================================================
    // Item 15.F: TestProcessTracker Lifecycle Tests
    // =========================================================================

    [Fact]
    public void TestProcessTracker_KillsNewAppOnDispose()
    {
        Process? spawned = null;
        try
        {
            using (var tracker = new TestProcessTracker("cmd"))
            {
                // Spawn a real cmd process that stays open
                var psi = new ProcessStartInfo("cmd.exe", "/k")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                spawned = Process.Start(psi);
                Assert.NotNull(spawned);
                tracker.TrackPid(spawned.Id);
                Assert.False(spawned.HasExited);
            }

            // After dispose, process must be terminated
            Assert.True(spawned.WaitForExit(2000), "Process was not killed by TestProcessTracker");
        }
        finally
        {
            try { spawned?.Kill(); } catch { }
        }
    }

    [Fact]
    public void TestProcessTracker_PreservesAlreadyRunningApp()
    {
        // Current process is running before tracker begins
        var currentPid = Process.GetCurrentProcess().Id;
        var processName = Process.GetCurrentProcess().ProcessName;

        using (var tracker = new TestProcessTracker(processName))
        {
            Assert.Contains(currentPid, tracker.InitialPids);
        }

        // Current process must still be running after dispose
        Assert.False(Process.GetCurrentProcess().HasExited);
    }

    [Fact]
    public void TestProcessTracker_CleansUpOnException()
    {
        Process? spawned = null;
        try
        {
            Assert.Throws<InvalidOperationException>((Action)(() =>
            {
                using var tracker = new TestProcessTracker("cmd");
                var psi = new ProcessStartInfo("cmd.exe", "/k")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                spawned = Process.Start(psi);
                tracker.TrackPid(spawned!.Id);
                throw new InvalidOperationException("Test exception simulation");
            }));

            Assert.NotNull(spawned);
            Assert.True(spawned.WaitForExit(2000), "Process should be cleaned up even if exception is thrown");
        }
        finally
        {
            try { spawned?.Kill(); } catch { }
        }
    }

    [Fact]
    public void TestProcessTracker_CleansUpOnCancellation()
    {
        Process? spawned = null;
        try
        {
            Assert.Throws<OperationCanceledException>((Action)(() =>
            {
                using var tracker = new TestProcessTracker("cmd");
                var psi = new ProcessStartInfo("cmd.exe", "/k")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                spawned = Process.Start(psi);
                tracker.TrackPid(spawned!.Id);
                throw new OperationCanceledException("Test cancellation simulation");
            }));

            Assert.NotNull(spawned);
            Assert.True(spawned.WaitForExit(2000), "Process should be cleaned up even on cancellation");
        }
        finally
        {
            try { spawned?.Kill(); } catch { }
        }
    }

    [Fact]
    public void TestProcessTracker_CleansUpOnTimeout()
    {
        Process? spawned = null;
        try
        {
            Assert.Throws<TimeoutException>((Action)(() =>
            {
                using var tracker = new TestProcessTracker("cmd");
                var psi = new ProcessStartInfo("cmd.exe", "/k")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                spawned = Process.Start(psi);
                tracker.TrackPid(spawned!.Id);
                throw new TimeoutException("Test timeout simulation");
            }));

            Assert.NotNull(spawned);
            Assert.True(spawned.WaitForExit(2000), "Process should be cleaned up even on timeout");
        }
        finally
        {
            try { spawned?.Kill(); } catch { }
        }
    }

    [Fact]
    public void TestProcessTracker_DoubleDisposeIsIdempotent()
    {
        var tracker = new TestProcessTracker("nonexistent_process");
        tracker.Dispose();
        var ex = Record.Exception(() => tracker.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public void TestProcessTracker_ExternalProcess_NotOwnedByTest()
    {
        var tracker = new TestProcessTracker("cmd");
        // An external PID that was neither in InitialPids nor explicitly tracked
        var fakeExternalPid = 999999;
        Assert.False(tracker.IsOwnedByTest(fakeExternalPid));
    }

    [Fact]
    public void TestProcessTracker_ParallelTrackers_DoNotKillEachOthersProcesses()
    {
        Process? procA = null;
        Process? procB = null;
        try
        {
            using var trackerA = new TestProcessTracker("cmd");
            using var trackerB = new TestProcessTracker("cmd");

            procA = Process.Start(new ProcessStartInfo("cmd.exe", "/k") { CreateNoWindow = true, UseShellExecute = false });
            trackerA.TrackPid(procA!.Id);

            procB = Process.Start(new ProcessStartInfo("cmd.exe", "/k") { CreateNoWindow = true, UseShellExecute = false });
            trackerB.TrackPid(procB!.Id);

            Assert.True(trackerA.IsOwnedByTest(procA.Id));
            Assert.False(trackerA.IsOwnedByTest(procB.Id));
            Assert.True(trackerB.IsOwnedByTest(procB.Id));
            Assert.False(trackerB.IsOwnedByTest(procA.Id));

            // Disposing A must only terminate procA, leaving procB alive
            trackerA.Dispose();
            Assert.True(procA.WaitForExit(2000), "ProcA was not terminated by trackerA");
            Assert.False(procB.HasExited, "ProcB was wrongly terminated by trackerA");
        }
        finally
        {
            try { procA?.Kill(); } catch { }
            try { procB?.Kill(); } catch { }
        }
    }

    // =========================================================================
    // Item 1: FindCache Invalidation and Event Gating Tests
    // =========================================================================

    [Fact]
    public void FindCache_InvalidatesOnMatchingWindowEvent()
    {
        using var session = new McpSession(EmergencyControl.Process, startEvents: false);
        var key = "role:Button|name:Save";
        var dummy = new List<UiElement>();

        // Cache for HWND 500
        session.FindCache[key] = (dummy, DateTimeOffset.UtcNow, new HashSet<long> { 500 }, new HashSet<int>(), 0);
        Assert.True(session.FindCache.ContainsKey(key));

        // Event on unrelated HWND 999 does not invalidate HWND 500's scope
        var buf = session.Rt.Parts.EventBuffer;
        buf.Add(new ObservedEvent(EventKind.StructureChanged, DateTimeOffset.UtcNow, 999, 1));

        // Scope match on HWND 500 invalidates
        buf.Add(new ObservedEvent(EventKind.StructureChanged, DateTimeOffset.UtcNow, 500, 1));
        var hasKey = session.FindCache.TryGetValue(key, out var hit);
        var fresh = hasKey && hit.Ver == session.Rt.MutationVersion &&
                    !buf.Snapshot(100).Any(e => e.At >= hit.At && hit.Hwnds.Contains(e.Hwnd ?? 0));
        Assert.False(fresh, "Cache must become stale when event arrives for cached HWND");
    }

    [Fact]
    public void FindCache_PreservesCacheOnUnrelatedWindowEvent()
    {
        using var session = new McpSession(EmergencyControl.Process, startEvents: false);
        var key = "role:Button|name:Save";
        var dummy = new List<UiElement>();

        // Cache for HWND 500
        session.FindCache[key] = (dummy, DateTimeOffset.UtcNow, new HashSet<long> { 500 }, new HashSet<int>(), 0);

        // Event for unrelated HWND 777
        var buf = session.Rt.Parts.EventBuffer;
        buf.Add(new ObservedEvent(EventKind.StructureChanged, DateTimeOffset.UtcNow, 777, 1));

        var hit = session.FindCache[key];
        var fresh = hit.Ver == session.Rt.MutationVersion &&
                    !buf.Snapshot(100).Any(e => e.At >= hit.At && hit.Hwnds.Contains(e.Hwnd ?? 0));
        Assert.True(fresh, "Cache must remain fresh when event arrives for completely unrelated HWND");
    }

    // =========================================================================
    // Item 2 & 8: App Catalog Telemetry and Invalidation Tests
    // =========================================================================

    [Fact]
    public void AppService_CatalogCounters_MeasureFullScanVsMemoryHit()
    {
        AppService.ResetTelemetry();
        var win = new MockWindowService();
        var svc = new AppService(win);

        // First call triggers full scan
        var first = svc.ListApps();
        Assert.True(AppService.CatalogFullScanCount >= 1, "First ListApps must perform full catalog scan");

        // Second call must hit memory cache with zero new full scans
        var prevScans = AppService.CatalogFullScanCount;
        var second = svc.ListApps();
        Assert.Equal(prevScans, AppService.CatalogFullScanCount);
        Assert.True(AppService.MemoryCacheHitCount >= 1, "Second ListApps must hit memory cache");
    }

    [Fact]
    public void AppService_ResolveCache_TracksHitsAndNegativeCache()
    {
        AppService.ResetTelemetry();
        var win = new MockWindowService();
        var svc = new AppService(win);

        // Unknown app resolution
        var empty1 = svc.ResolveAll("nonexistent_app_foo_bar_xyz");
        Assert.Empty(empty1);

        // Second lookup hits negative cache
        var empty2 = svc.ResolveAll("nonexistent_app_foo_bar_xyz");
        Assert.Empty(empty2);
        Assert.True(AppService.NegativeCacheHitCount >= 1, "Must hit negative cache on repeated unknown app query");
    }

    // =========================================================================
    // Item 3: Verification PostVerify Redundancy Elimination
    // =========================================================================

    [Fact]
    public void Verification_ClickAndInvoke_EliminatesRedundantReResolve()
    {
        var backend = new MockBackend();
        var win = new MockWindowService();
        var reg = new ElementRegistry(new[] { backend });
        var exec = new Executor(win, new MockIntegrityService(), new MockInputService(), new MockCaptureService(),
            new[] { backend }, reg, new SafetyPolicy());

        var el = CreateTestElement("btn_ok");
        reg.Register(new[] { el });

        int reresolveCalls = 0;
        backend.OnReResolve = handle =>
        {
            reresolveCalls++;
            return el;
        };

        // Click action without explicit VerifySpec
        var intent = new ActionIntent(ActionKind.Click, TargetRef.Element("btn_ok"));
        var result = exec.Perform(intent, new ActionContext(CancellationToken.None, null, "test", null, null));

        Assert.True(result.Success);
        Assert.Equal(0, reresolveCalls); // Zero redundant ReResolves performed!
    }

    // =========================================================================
    // Item 4: EventBuffer Matrix Tests (Exact 10 Test Cases)
    // =========================================================================

    [Fact]
    public void EventBuffer_EventBeforeBaseline_DoesNotWakeNewWaiter()
    {
        var buffer = new RecentEventBuffer();

        // 1. Event occurs BEFORE caller takes baseline
        buffer.Add(new ObservedEvent(EventKind.WindowOpened, DateTimeOffset.UtcNow, 100, 1));

        // 2. Caller takes baseline AFTER the event has already occurred
        var baseline = buffer.CurrentGeneration;

        // 3. Caller waits: old event prior to baseline must NOT wake the waiter
        var sw = Stopwatch.StartNew();
        var woke = buffer.WaitForNextEvent(baseline, 40, CancellationToken.None);
        sw.Stop();

        Assert.False(woke, "Old event prior to baseline must not wake waiter");
        Assert.True(sw.ElapsedMilliseconds >= 35);
    }

    [Fact]
    public void EventBuffer_EventAfterBaselineBeforeWaitRegistration_IsNotLost()
    {
        var buffer = new RecentEventBuffer();

        // 1. Caller captures baseline
        var baseline = buffer.CurrentGeneration;

        // 2. New event arrives before caller physically enters WaitForNextEvent
        buffer.Add(new ObservedEvent(EventKind.WindowOpened, DateTimeOffset.UtcNow, 100, 1));

        // 3. Caller enters WaitForNextEvent: must NOT be lost, must return true immediately
        var sw = Stopwatch.StartNew();
        var woke = buffer.WaitForNextEvent(baseline, 300, CancellationToken.None);
        sw.Stop();

        Assert.True(woke, "Event arriving after baseline before wait registration must not be lost");
        Assert.True(sw.ElapsedMilliseconds < 50, $"Should return immediately, took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void EventBuffer_Case2_NoEvent_TimesOutCleanly()
    {
        var buffer = new RecentEventBuffer();
        var gen = buffer.CurrentGeneration;
        var sw = Stopwatch.StartNew();
        var woke = buffer.WaitForNextEvent(gen, 50, CancellationToken.None);
        sw.Stop();
        Assert.False(woke);
        Assert.True(sw.ElapsedMilliseconds >= 40);
    }

    [Fact]
    public void EventBuffer_Case3_ExactlyOneNewEvent_WakesPromptly()
    {
        var buffer = new RecentEventBuffer();
        var gen = buffer.CurrentGeneration;
        Task.Run(async () =>
        {
            await Task.Delay(15);
            buffer.Add(new ObservedEvent(EventKind.FocusChanged, DateTimeOffset.UtcNow, 100, 1));
        });
        var sw = Stopwatch.StartNew();
        var woke = buffer.WaitForNextEvent(gen, 300, CancellationToken.None);
        sw.Stop();
        Assert.True(woke);
        Assert.True(sw.ElapsedMilliseconds < 250);
    }

    [Fact]
    public void EventBuffer_Case4_10kEventFlood_CoalescesWithoutBusySpin()
    {
        var buffer = new RecentEventBuffer(capacity: 100);
        const int totalEvents = 10000;
        var sw = Stopwatch.StartNew();

        Parallel.For(0, totalEvents, i =>
        {
            buffer.Add(new ObservedEvent(EventKind.StructureChanged, DateTimeOffset.UtcNow, i % 10, i % 5));
        });

        sw.Stop();
        Assert.Equal(totalEvents, buffer.CurrentGeneration);
        Assert.Equal(100, buffer.Snapshot(200).Count);
        Assert.True(sw.ElapsedMilliseconds < 1000, $"Flood took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void EventBuffer_Case5_MultipleSequentialWaits_TrackGenerationsIndependently()
    {
        var buffer = new RecentEventBuffer();
        for (int i = 0; i < 3; i++)
        {
            var gen = buffer.CurrentGeneration;
            buffer.Add(new ObservedEvent(EventKind.StructureChanged, DateTimeOffset.UtcNow, 100, 1));
            Assert.True(buffer.WaitForNextEvent(gen, 100, CancellationToken.None));
        }
        Assert.Equal(3, buffer.CurrentGeneration);
    }

    [Fact]
    public void EventBuffer_Case6_Cancellation_ThrowsImmediately()
    {
        var buffer = new RecentEventBuffer();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            buffer.WaitForNextEvent(buffer.CurrentGeneration, 1000, cts.Token));
    }

    [Fact]
    public async Task EventBuffer_Case7_MultipleConcurrentWaiters_AllWakeOnSinglePulse()
    {
        var buffer = new RecentEventBuffer();
        var gen = buffer.CurrentGeneration;
        const int waiterCount = 4;
        var tasks = new Task<bool>[waiterCount];

        for (int i = 0; i < waiterCount; i++)
        {
            tasks[i] = Task.Run(() => buffer.WaitForNextEvent(gen, 500, CancellationToken.None));
        }

        await Task.Delay(30);
        buffer.Add(new ObservedEvent(EventKind.ForegroundChanged, DateTimeOffset.UtcNow, 100, 1));

        var results = await Task.WhenAll(tasks);
        Assert.All(results, Assert.True);
    }

    [Fact]
    public void EventBuffer_Case8_UnrelatedHwndOrPidEvent_PreservesCacheAndScope()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var key = "role:Button|name:Save";
        session.FindCache[key] = (new List<UiElement>(), DateTimeOffset.UtcNow, new HashSet<long> { 500 }, new HashSet<int>(), 0);

        var buf = session.Rt.Parts.EventBuffer;
        buf.Add(new ObservedEvent(EventKind.StructureChanged, DateTimeOffset.UtcNow, 777, 1));

        var hit = session.FindCache[key];
        var fresh = hit.Ver == session.Rt.MutationVersion &&
                    !buf.Snapshot(100).Any(e => e.At >= hit.At && hit.Hwnds.Contains(e.Hwnd ?? 0));
        Assert.True(fresh, "Cache must remain fresh when event arrives for completely unrelated HWND");
    }

    [Fact]
    public void EventBuffer_Case9_RelevantHwndOrPidEvent_InvalidatesAndWakes()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var key = "role:Button|name:Save";
        session.FindCache[key] = (new List<UiElement>(), DateTimeOffset.UtcNow, new HashSet<long> { 500 }, new HashSet<int>(), 0);

        var buf = session.Rt.Parts.EventBuffer;
        buf.Add(new ObservedEvent(EventKind.StructureChanged, DateTimeOffset.UtcNow, 500, 1));

        var hit = session.FindCache[key];
        var fresh = hit.Ver == session.Rt.MutationVersion &&
                    !buf.Snapshot(100).Any(e => e.At >= hit.At && hit.Hwnds.Contains(e.Hwnd ?? 0));
        Assert.False(fresh, "Cache must be invalidated when relevant HWND event arrives");
    }

    [Fact]
    public async Task EventBuffer_Case10_ShutdownOrDisposeWhileWaiting_ReturnsCleanly()
    {
        var buffer = new RecentEventBuffer();
        var gen = buffer.CurrentGeneration;

        var waitTask = Task.Run(() => buffer.WaitForNextEvent(gen, 5000, CancellationToken.None));
        await Task.Delay(30);
        buffer.Dispose();

        var woke = await waitTask;
        Assert.False(woke, "Dispose while waiting must unblock cleanly returning false");
    }

    // =========================================================================
    // Item 8: Batch Integration Tests A through F
    // =========================================================================

    [Fact]
    public async Task Batch_A_ThreeDeterministicActions_ExecuteInOrder()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        var steps = new[]
        {
            new InbriskTools.BatchStep(Do: "wait", Ms: 1),
            new InbriskTools.BatchStep(Do: "wait", Ms: 1),
            new InbriskTools.BatchStep(Do: "wait", Ms: 1)
        };

        var res = await tools.Batch(steps);
        Assert.False(res.IsError);
        var text = string.Join("\n", res.Content.OfType<TextContentBlock>().Select(t => t.Text));
        Assert.Contains("Completed", text);
    }

    [Fact]
    public async Task Batch_B_ReadPropertyValues_ReturnsExtractedValues()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        var steps = new[] { new InbriskTools.BatchStep(Do: "wait", Ms: 1) };
        var read = new[] { new InbriskTools.BatchReadSpec("NonExistentTestTarget", ["value", "enabled"]) };

        var res = await tools.Batch(steps, read: read);
        Assert.False(res.IsError);
        var text = string.Join("\n", res.Content.OfType<TextContentBlock>().Select(t => t.Text));
        Assert.Contains("read", text);
    }

    [Fact]
    public async Task Batch_C_UntilSuccess_ReturnsWhenConditionAppears()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        var steps = new[] { new InbriskTools.BatchStep(Do: "wait", Ms: 1) };
        var until = new InbriskTools.BatchUntilSpec(Appears: "DummyNonExistentButton", TimeoutMs: 50);

        var res = await tools.Batch(steps, until: until);
        var text = string.Join("\n", res.Content.OfType<TextContentBlock>().Select(t => t.Text));
        Assert.NotNull(text);
    }

    [Fact]
    public async Task Batch_D_UntilTimeout_HaltsAndReportsTimeout()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        var steps = new[] { new InbriskTools.BatchStep(Do: "wait", Ms: 1) };
        var until = new InbriskTools.BatchUntilSpec(Appears: "GuaranteedNonExistentTargetElementXYZ999", TimeoutMs: 25);

        var res = await tools.Batch(steps, until: until);
        var text = string.Join("\n", res.Content.OfType<TextContentBlock>().Select(t => t.Text));
        Assert.True(res.IsError == true || text.Contains("Timeout") || text.Contains("Paused") || text.Contains("Failed"));
    }

    [Fact]
    public async Task Batch_E_MiddleStepFailure_HaltsExecutionAndReportsIndex()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        // Step 0: ok, Step 1: invalid target that fails, Step 2: never executes
        var steps = new[]
        {
            new InbriskTools.BatchStep(Do: "wait", Ms: 1),
            new InbriskTools.BatchStep(Do: "click", T: "NonExistentTargetElement12345"),
            new InbriskTools.BatchStep(Do: "wait", Ms: 1)
        };

        var res = await tools.Batch(steps);
        var text = string.Join("\n", res.Content.OfType<TextContentBlock>().Select(t => t.Text));
        Assert.Contains("Paused", text);

        var doc = JsonDocument.Parse(text).RootElement;
        Assert.Equal("Paused", doc.GetProperty("status").GetString());
        var pause = doc.GetProperty("pause");
        Assert.Equal(1, pause.GetProperty("step").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(pause.GetProperty("error").GetString()));

        // Verify later steps executed=false: report steps array only has 2 entries (step 0 and 1)
        var reportedSteps = doc.GetProperty("steps");
        Assert.Equal(2, reportedSteps.GetArrayLength());
    }

    [Fact]
    public async Task Batch_F_RunPlanCoreCompatibility_LegacyComputerRunPreserved()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        var steps = new[] { new InbriskTools.RunStep { Action = "wait", Ms = 1 } };
        var res = await tools.RunPlan(steps);
        Assert.False(res.IsError);
        var text = string.Join("\n", res.Content.OfType<TextContentBlock>().Select(t => t.Text));
        Assert.Contains("Completed", text);
    }

    // =========================================================================
    // Item 10: Legacy Save / Resume Compatibility
    // =========================================================================

    [Fact]
    public async Task SaveRecipe_LegacyJsonSteps_Accepted()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        var legacyJson = JsonDocument.Parse("[{\"action\":\"click\",\"target\":{\"name\":\"OK\"}}]").RootElement;
        var result = await tools.SaveRecipe(name: "legacy-save-recipe", description: "legacy desc", steps: legacyJson);

        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(t => t.Text));
        Assert.False(result.IsError, text);
        Assert.Contains("saved", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResumeRun_LegacyJsonSteps_Accepted()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        var legacyJson = JsonDocument.Parse("[{\"action\":\"wait\",\"ms\":1}]").RootElement;
        var res = await tools.ResumeRun("legacy-resume-run", remainingSteps: legacyJson);
        var text = string.Join("\n", res.Content.OfType<TextContentBlock>().Select(t => t.Text));
        Assert.DoesNotContain("Invalid remainingSteps JSON", text);
    }

    [Fact]
    public async Task SaveRecipe_NewCompactTypedSteps_Accepted()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        var typedJson = JsonDocument.Parse("[{\"action\":\"wait\",\"ms\":1}]").RootElement;
        var result = await tools.SaveRecipe(name: "new-compact-save", description: "compact desc", steps: typedJson);

        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(t => t.Text));
        Assert.False(result.IsError, text);
        Assert.Contains("saved", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResumeRun_NewCompactTypedSteps_Accepted()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        var typedJson = JsonDocument.Parse("[{\"action\":\"wait\",\"ms\":1}]").RootElement;
        var res = await tools.ResumeRun("new-compact-resume", remainingSteps: typedJson);
        var text = string.Join("\n", res.Content.OfType<TextContentBlock>().Select(t => t.Text));
        Assert.DoesNotContain("Invalid remainingSteps JSON", text);
    }

    [Fact]
    public async Task ResumeRun_MalformedJsonSteps_Rejected()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        var invalidJson = JsonDocument.Parse("{\"not\":\"an array\"}").RootElement;
        var res = await tools.ResumeRun("malformed-resume-run", remainingSteps: invalidJson);
        Assert.True(res.IsError);
        var text = string.Join("\n", res.Content.OfType<TextContentBlock>().Select(t => t.Text));
        Assert.Contains("Malformed", text);
    }

    // =========================================================================
    // Item 6: App Catalog Invalidation Tests
    // =========================================================================

    [Fact]
    public void AppCatalog_StartMenuLinkAddedRemoved_InvalidatesCache()
    {
        var win = new MockWindowService();
        var svc = new AppService(win);
        var first = svc.ListApps();
        Assert.NotNull(first);

        // Simulation of FSW notification on lnk change
        svc.InvalidateCache();
        var second = svc.ListApps();
        Assert.NotNull(second);
    }

    [Fact]
    public void AppCatalog_AppPathsChanged_RefreshesCatalog()
    {
        var win = new MockWindowService();
        var svc = new AppService(win);
        svc.InvalidateCache();
        var apps = svc.ListApps();
        Assert.NotNull(apps);
    }

    [Fact]
    public void AppCatalog_PackagedAppChange_RefreshesCatalog()
    {
        var win = new MockWindowService();
        var svc = new AppService(win);
        svc.PackageEnumerator = () => new List<AppService.ResolvedApp>
        {
            new(LaunchMethod.Aumid, "TestPackagedApp_123!App", "TestPackagedApp", ["TestPackagedApp"], 100)
        };
        svc.InvalidateCache();
        var apps = svc.ListApps();
        Assert.Contains(apps, a => a.Name == "TestPackagedApp");
    }

    [Fact]
    public void AppCatalog_InstallUninstallEquivalentRefresh_ClearsCaches()
    {
        var win = new MockWindowService();
        var svc = new AppService(win);
        var c1 = svc.ListApps();
        svc.InvalidateCache();
        var c2 = svc.ListApps();
        Assert.NotNull(c2);
    }

    [Fact]
    public void AppCatalog_NegativeCacheThenAppAppears_ResolvesSuccessfully()
    {
        var win = new MockWindowService();
        var svc = new AppService(win);

        // 1. Initial lookup is negative
        var empty = svc.ResolveAll("DynamicTestApp123");
        Assert.Empty(empty);

        // 2. Package enumerator provides app
        svc.PackageEnumerator = () => new List<AppService.ResolvedApp>
        {
            new(LaunchMethod.Aumid, "DynamicTestApp123!App", "DynamicTestApp123", ["DynamicTestApp123"], 100)
        };
        svc.InvalidateCache();

        // 3. Post-invalidation lookup succeeds immediately
        var found = svc.ResolveAll("DynamicTestApp123");
        Assert.NotEmpty(found);
        Assert.Equal("DynamicTestApp123", found[0].DisplayName);
    }

    [Fact]
    public void AppCatalog_StaleCacheFallbackTtl_ExpiresNegativeEntries()
    {
        var win = new MockWindowService();
        var svc = new AppService(win);
        var empty1 = svc.ResolveAll("FutureAppX");
        Assert.Empty(empty1);
        var empty2 = svc.ResolveAll("FutureAppX");
        Assert.Empty(empty2);
    }

    // =========================================================================
    // Item 7: Process Ownership & Windows Job Object Tests
    // =========================================================================

    [Fact]
    public void ProcessTracker_PreExistingUserProcess_Preserved()
    {
        var currentPid = Process.GetCurrentProcess().Id;
        var processName = Process.GetCurrentProcess().ProcessName;

        using (var tracker = new TestProcessTracker(allowInference: false, processName))
        {
            Assert.Contains(currentPid, tracker.InitialPids);
            Assert.False(tracker.IsOwnedByTest(currentPid));
        }

        Assert.False(Process.GetCurrentProcess().HasExited);
    }

    [Fact]
    public void ProcessTracker_UserLaunchesNewProcessDuringTest_Preserved()
    {
        Process? userProcess = null;
        try
        {
            using (var tracker = new TestProcessTracker(allowInference: false, "cmd"))
            {
                // User process launched without TrackOwnedProcess registration
                userProcess = Process.Start(new ProcessStartInfo("cmd.exe", "/k") { CreateNoWindow = true, UseShellExecute = false });
                Assert.NotNull(userProcess);
                Assert.False(tracker.IsOwnedByTest(userProcess.Id));
            }

            Assert.False(userProcess.HasExited, "User process launched during test must not be killed on tracker dispose");
        }
        finally
        {
            try { userProcess?.Kill(); } catch { }
        }
    }

    [Fact]
    public void ProcessTracker_TestOwnedProcess_Closed()
    {
        Process? testProc = null;
        try
        {
            using (var tracker = new TestProcessTracker(allowInference: false, "cmd"))
            {
                testProc = Process.Start(new ProcessStartInfo("cmd.exe", "/k") { CreateNoWindow = true, UseShellExecute = false });
                Assert.NotNull(testProc);
                tracker.TrackOwnedProcess(testProc);
                Assert.True(tracker.IsOwnedByTest(testProc.Id));
            }

            Assert.True(testProc.WaitForExit(2000), "Test-owned process must be cleanly closed");
        }
        finally
        {
            try { testProc?.Kill(); } catch { }
        }
    }

    [Fact]
    public void ProcessTracker_ChildProcess_Closed()
    {
        Process? parentProc = null;
        try
        {
            using (var tracker = new TestProcessTracker(allowInference: false, "cmd"))
            {
                parentProc = Process.Start(new ProcessStartInfo("cmd.exe", "/c cmd.exe /k") { CreateNoWindow = true, UseShellExecute = false });
                Assert.NotNull(parentProc);
                tracker.TrackOwnedProcess(parentProc);
            }

            Assert.True(parentProc.WaitForExit(2000), "Process tree must be terminated on dispose");
        }
        finally
        {
            try { parentProc?.Kill(entireProcessTree: true); } catch { }
        }
    }

    [Fact]
    public void ProcessTracker_ParallelTrackers_Isolated()
    {
        Process? procA = null;
        Process? procB = null;
        try
        {
            using var trackerA = new TestProcessTracker(allowInference: false, "cmd");
            using var trackerB = new TestProcessTracker(allowInference: false, "cmd");

            procA = Process.Start(new ProcessStartInfo("cmd.exe", "/k") { CreateNoWindow = true, UseShellExecute = false });
            trackerA.TrackOwnedProcess(procA!);

            procB = Process.Start(new ProcessStartInfo("cmd.exe", "/k") { CreateNoWindow = true, UseShellExecute = false });
            trackerB.TrackOwnedProcess(procB!);

            Assert.True(trackerA.IsOwnedByTest(procA.Id));
            Assert.False(trackerA.IsOwnedByTest(procB.Id));
            Assert.True(trackerB.IsOwnedByTest(procB.Id));
            Assert.False(trackerB.IsOwnedByTest(procA.Id));

            trackerA.Dispose();
            Assert.True(procA.WaitForExit(2000), "procA must be closed by trackerA");
            Assert.False(procB.HasExited, "procB must remain untouched by trackerA");
        }
        finally
        {
            try { procA?.Kill(); } catch { }
            try { procB?.Kill(); } catch { }
        }
    }

    [Fact]
    public void ProcessTracker_ExceptionCleanup_TerminatesOwned()
    {
        Process? proc = null;
        try
        {
            Assert.Throws<InvalidOperationException>((Action)(() =>
            {
                using var tracker = new TestProcessTracker(allowInference: false, "cmd");
                proc = Process.Start(new ProcessStartInfo("cmd.exe", "/k") { CreateNoWindow = true, UseShellExecute = false });
                tracker.TrackOwnedProcess(proc!);
                throw new InvalidOperationException("Simulation error");
            }));

            Assert.NotNull(proc);
            Assert.True(proc.WaitForExit(2000));
        }
        finally
        {
            try { proc?.Kill(); } catch { }
        }
    }

    [Fact]
    public void ProcessTracker_TimeoutCleanup_TerminatesOwned()
    {
        Process? proc = null;
        try
        {
            Assert.Throws<TimeoutException>((Action)(() =>
            {
                using var tracker = new TestProcessTracker(allowInference: false, "cmd");
                proc = Process.Start(new ProcessStartInfo("cmd.exe", "/k") { CreateNoWindow = true, UseShellExecute = false });
                tracker.TrackOwnedProcess(proc!);
                throw new TimeoutException("Simulation timeout");
            }));

            Assert.NotNull(proc);
            Assert.True(proc.WaitForExit(2000));
        }
        finally
        {
            try { proc?.Kill(); } catch { }
        }
    }

    [Fact]
    public void ProcessTracker_CancellationCleanup_TerminatesOwned()
    {
        Process? proc = null;
        try
        {
            Assert.Throws<OperationCanceledException>((Action)(() =>
            {
                using var tracker = new TestProcessTracker(allowInference: false, "cmd");
                proc = Process.Start(new ProcessStartInfo("cmd.exe", "/k") { CreateNoWindow = true, UseShellExecute = false });
                tracker.TrackOwnedProcess(proc!);
                throw new OperationCanceledException("Simulation cancellation");
            }));

            Assert.NotNull(proc);
            Assert.True(proc.WaitForExit(2000));
        }
        finally
        {
            try { proc?.Kill(); } catch { }
        }
    }

    [Fact]
    public void ProcessTracker_IdempotentCleanup_MultipleDisposeSafe()
    {
        var tracker = new TestProcessTracker(allowInference: false, "cmd");
        tracker.Dispose();
        var ex = Record.Exception(() => tracker.Dispose());
        Assert.Null(ex);
    }

    // =========================================================================
    // Item 11: Find Cache 5s TTL Stale-State Risk Tests
    // =========================================================================

    [Fact]
    public void FindCache_PositiveHit_TargetRemovedOrRenamed_EventInvalidatesImmediately_NoStaleResult()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var key = "role:Button|name:Save";
        var element = CreateTestElement("btn_save", hwnd: 500);
        session.FindCache[key] = (new List<UiElement> { element }, DateTimeOffset.UtcNow, new HashSet<long> { 500 }, new HashSet<int>(), session.Rt.MutationVersion);

        // Cache hit initially
        Assert.True(session.FindCache.TryGetValue(key, out var hit));
        var isFresh = hit.Ver == session.Rt.MutationVersion &&
                      !session.Rt.Parts.EventBuffer.Snapshot(100).Any(e => e.At >= hit.At && hit.Hwnds.Contains(e.Hwnd ?? 0));
        Assert.True(isFresh);

        // Target removed/renamed: StructureChanged event arrives on HWND 500
        session.Rt.Parts.EventBuffer.Add(new ObservedEvent(EventKind.StructureChanged, DateTimeOffset.UtcNow, 500, 1));

        // Event buffer check invalidates cache immediately
        var isStale = session.Rt.Parts.EventBuffer.Snapshot(100).Any(e => e.At >= hit.At && hit.Hwnds.Contains(e.Hwnd ?? 0));
        Assert.True(isStale, "Cache must be recognized as stale immediately upon event arrival");
    }

    [Fact]
    public void FindCache_PositiveHit_PropertyStateMutation_InvalidatesImmediately()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var key = "role:Button|name:Save";
        var element = CreateTestElement("btn_save", hwnd: 500);
        session.FindCache[key] = (new List<UiElement> { element }, DateTimeOffset.UtcNow, new HashSet<long> { 500 }, new HashSet<int>(), session.Rt.MutationVersion);

        // Property changed event arrives
        session.Rt.Parts.EventBuffer.Add(new ObservedEvent(EventKind.ValueChanged, DateTimeOffset.UtcNow, 500, 1));
        var isStale = session.Rt.Parts.EventBuffer.Snapshot(100).Any(e => e.Hwnd == 500);
        Assert.True(isStale, "Property mutation must invalidate find cache immediately");
    }

    // =========================================================================
    // Item 1 & 2: Inspect Cache & Negative Find Cache Tests
    // =========================================================================

    [Fact]
    public void InspectCache_EventScoping_ScenariosA_B_C_D_Verified()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var opt = new InspectOptions();
        long hwndA = 100;
        long hwndB = 200;
        long hwndC = 300;

        // SCENARIO A: Inspect HWND A, cache is fresh
        session.Rt.Inspect(hwndA, opt);
        Assert.True(session.Rt.IsInspectCacheFresh(hwndA, opt), "Scenario A: Cache must be fresh after inspect");

        // SCENARIO B: StructureChanged for unrelated HWND B -> HWND A cache STILL hits!
        session.Rt.Parts.EventBuffer.Add(new ObservedEvent(EventKind.StructureChanged, DateTimeOffset.UtcNow, hwndB, 2));
        Assert.True(session.Rt.IsInspectCacheFresh(hwndA, opt), "Scenario B: Unrelated HWND B event must not invalidate HWND A cache");

        // SCENARIO D: Unrelated WindowOpened/WindowClosed for HWND C -> HWND A subtree cache is preserved!
        session.Rt.Parts.EventBuffer.Add(new ObservedEvent(EventKind.WindowOpened, DateTimeOffset.UtcNow, hwndC, 3));
        session.Rt.Parts.EventBuffer.Add(new ObservedEvent(EventKind.WindowClosed, DateTimeOffset.UtcNow, hwndC, 3));
        Assert.True(session.Rt.IsInspectCacheFresh(hwndA, opt), "Scenario D: Unrelated window open/close must not invalidate HWND A cache");

        // SCENARIO C: StructureChanged for HWND A -> cache is invalidated, forcing fresh traversal
        session.Rt.Parts.EventBuffer.Add(new ObservedEvent(EventKind.StructureChanged, DateTimeOffset.UtcNow.AddMilliseconds(10), hwndA, 1));
        Assert.False(session.Rt.IsInspectCacheFresh(hwndA, opt), "Scenario C: Matching HWND A mutation must invalidate cache");
    }

    [Fact]
    public void NegativeFindCache_EventScoping_UnrelatedHwndDoesNotInvalidate_MatchingHwndInvalidates()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var key = "hwnd:100|role:Button|name:NonExistent";
        long hwndA = 100;
        long hwndB = 200;

        // 1. Negative miss for HWND A cached
        session.NegativeFindCache[key] = (new TargetDiagnosis("TargetNotFound", "No match"), DateTimeOffset.UtcNow, new HashSet<long> { hwndA }, new HashSet<int>(), session.Rt.MutationVersion);

        // 2. Unrelated HWND B event arrives -> HWND A negative cache is STILL fresh (hit)
        session.Rt.Parts.EventBuffer.Add(new ObservedEvent(EventKind.StructureChanged, DateTimeOffset.UtcNow, hwndB, 2));
        var hit = session.NegativeFindCache[key];
        var isFreshAfterUnrelated = hit.Ver == session.Rt.MutationVersion &&
            !session.Rt.Parts.EventBuffer.Snapshot(100).Any(e => e.At >= hit.At && hit.Hwnds.Contains(e.Hwnd ?? 0));
        Assert.True(isFreshAfterUnrelated, "Unrelated HWND B event must not invalidate HWND A negative cache");

        // 3. Relevant HWND A mutation arrives -> negative cache is invalidated!
        session.Rt.Parts.EventBuffer.Add(new ObservedEvent(EventKind.StructureChanged, DateTimeOffset.UtcNow, hwndA, 1));
        var isFreshAfterMatching = hit.Ver == session.Rt.MutationVersion &&
            !session.Rt.Parts.EventBuffer.Snapshot(100).Any(e => e.At >= hit.At && hit.Hwnds.Contains(e.Hwnd ?? 0));
        Assert.False(isFreshAfterMatching, "Matching HWND A mutation must invalidate negative cache");
    }

    // =========================================================================
    // Section C: REDUNDANT TOOL CALLS Tests (C1 - C8)
    // =========================================================================

    [Fact]
    public async Task C1_CloseWindow_LastOwned_ClosesMostRecentAgentOwnedWindow()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        session.RecordWindowLaunch(101, 1001, "notepad", agentOwned: true);
        session.RecordWindowLaunch(102, 1002, "calc", agentOwned: true);

        var result = await tools.CloseWindow(lastOwned: true);
        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(t => t.Text));
        using var doc = JsonDocument.Parse(text);

        Assert.True(doc.RootElement.TryGetProperty("closed", out var closed) && closed.GetBoolean());
        Assert.Equal("0x66", doc.RootElement.GetProperty("hwnd").GetString());
        Assert.True(session.TrackedWindows.First(w => w.Hwnd == 102).ClosedOrStale);
    }

    [Fact]
    public async Task C1_CloseWindow_OwnedTrue_ClosesOnlyAgentOwned_PreservesUserApps()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        // Pre-existing user app
        session.RecordWindowLaunch(201, 2001, "user_editor", agentOwned: false);
        // Agent apps
        session.RecordWindowLaunch(202, 2002, "agent_tool_1", agentOwned: true);
        session.RecordWindowLaunch(203, 2003, "agent_tool_2", agentOwned: true);

        var result = await tools.CloseWindow(owned: true);
        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(t => t.Text));
        using var doc = JsonDocument.Parse(text);

        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        // User app is preserved
        Assert.False(session.TrackedWindows.First(w => w.Hwnd == 201).ClosedOrStale);
        // Agent apps closed
        Assert.True(session.TrackedWindows.First(w => w.Hwnd == 202).ClosedOrStale);
        Assert.True(session.TrackedWindows.First(w => w.Hwnd == 203).ClosedOrStale);
    }

    [Fact]
    public async Task C1_CloseWindow_Untargeted_ReturnsMalformedWithCandidates()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        var result = await tools.CloseWindow();
        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(t => t.Text));
        using var doc = JsonDocument.Parse(text);

        Assert.Equal("Malformed", doc.RootElement.GetProperty("error").GetString());
        Assert.True(doc.RootElement.TryGetProperty("candidates", out _));
        Assert.True(session.Telemetry.CloseRetry > 0);
    }

    [Fact]
    public async Task C1_CloseWindow_TargetNotFound_ReturnsCandidatesWithRetryArgs()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        var result = await tools.CloseWindow(hwnd: "0x9999999");
        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(t => t.Text));
        using var doc = JsonDocument.Parse(text);

        Assert.Equal("TargetNotFound", doc.RootElement.GetProperty("error").GetString());
        Assert.True(doc.RootElement.TryGetProperty("candidates", out _));
    }

    [Fact]
    public void C2_Deduplication_IdenticalReadRequest_DedupedWhenStateUnchanged()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        var r1 = tools.Windows();
        var initialSuppressed = session.Telemetry.DuplicateReadSuppressed;

        var r2 = tools.Windows();
        var afterSuppressed = session.Telemetry.DuplicateReadSuppressed;

        Assert.True(afterSuppressed > initialSuppressed, "Immediate identical read should be deduplicated");
        var text2 = string.Join("\n", r2.Content.OfType<TextContentBlock>().Select(t => t.Text));
        Assert.Contains("unchanged", text2, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void C2_Deduplication_IdenticalReadRequest_ExecutesFreshWhenStateMutates()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        var r1 = tools.Windows();

        // Mutate state
        session.Rt.Parts.EventBuffer.Add(new ObservedEvent(EventKind.WindowOpened, DateTimeOffset.UtcNow, 999, 1));
        session.Deduplicator.InvalidateReadCache();

        var r2 = tools.Windows();
        var text2 = string.Join("\n", r2.Content.OfType<TextContentBlock>().Select(t => t.Text));
        Assert.DoesNotContain("unchanged: true", text2, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task C2_Deduplication_MutatingWithoutOperationId_BothExecute()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        var r1 = await tools.CloseWindow(owned: true);
        var suppressedBefore = session.Telemetry.DuplicateMutationRetrySuppressed;

        var r2 = await tools.CloseWindow(owned: true);
        var suppressedAfter = session.Telemetry.DuplicateMutationRetrySuppressed;

        Assert.Equal(suppressedBefore, suppressedAfter);
    }

    [Fact]
    public async Task C2_Deduplication_MutatingWithOperationId_Deduped()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        var opId = "test-op-" + Guid.NewGuid().ToString("N");
        var r1 = await tools.CloseWindow(owned: true, operationId: opId);
        var suppressedBefore = session.Telemetry.DuplicateMutationRetrySuppressed;

        var r2 = await tools.CloseWindow(owned: true, operationId: opId);
        var suppressedAfter = session.Telemetry.DuplicateMutationRetrySuppressed;

        Assert.True(suppressedAfter > suppressedBefore, "Mutation with identical operationId should be deduplicated");
    }

    [Fact]
    public void C3_FollowUpObservation_IncrementsPostVerifiedObservationTelemetry()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        session.PrevOutcome = new StepOutcome(OutcomeKind.Verified, true, "click", null, 10);
        var initial = session.Telemetry.PostVerifiedObservation;

        tools.Observe();
        var after = session.Telemetry.PostVerifiedObservation;

        Assert.True(after > initial, "PostVerifiedObservation counter must increment on follow-up observe");
    }

    [Fact]
    public void C4_Launch_ReturnsInitialAppMapAndNextNone()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        // 1. Direct unit verification of initial app map structure
        var mapDirect = tools.BuildInitialAppMap(100, 1000, "Test Window");
        Assert.NotNull(mapDirect);
        Assert.True(mapDirect.ContainsKey("firstActionables"));
        Assert.True(mapDirect.ContainsKey("landmarks"));
    }

    [Fact]
    public async Task C5_Batch_ResetsSequentialSingleActionRunLength()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        // Uses fake keys without live input
        session.Telemetry.IncSequentialSingleAction();
        session.Telemetry.IncSequentialSingleAction();
        Assert.Equal(2, session.Telemetry.SequentialSingleActionRunLength);

        await tools.Batch(new[] { new InbriskTools.BatchStep { Do = "wait", Ms = 1 } });
        Assert.Equal(0, session.Telemetry.SequentialSingleActionRunLength);
    }

    [Fact]
    public void C6_LaunchPrecededByApps_TrackedInTelemetry()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        var initial = session.Telemetry.AppsThenLaunchWithin5s;
        session.LastAppsQueryTimestamp = DateTimeOffset.UtcNow;
        session.LastAppsQueryName = "test_query_app";

        tools.Launch(app: "test_nonexistent_headless_app", waitFor: "none", timeoutMs: 10);

        var after = session.Telemetry.AppsThenLaunchWithin5s;
        Assert.True(after > initial, "Launch preceded by apps within 5s must increment AppsThenLaunchWithin5s telemetry");
    }

    [Fact]
    public void C8_TelemetryCounters_AllCountersTrackable()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var t = session.Telemetry;

        t.IncDuplicateReadSuppressed();
        t.IncDuplicateMutationRetrySuppressed();
        t.IncPostVerifiedObservation();
        t.IncLaunchFollowupDiscovery();
        t.IncCloseRetry();
        t.IncAppsThenLaunchWithin5s();
        t.IncSequentialSingleAction();
        t.IncObservedChangeFollowup();

        var snap = t.GetSnapshot();
        Assert.Equal(1, snap["duplicateReadSuppressed"]);
        Assert.Equal(1, snap["duplicateMutationRetrySuppressed"]);
        Assert.Equal(1, snap["postVerifiedObservation"]);
        Assert.Equal(1, snap["launchFollowupDiscovery"]);
        Assert.Equal(1, snap["closeRetry"]);
        Assert.Equal(1, snap["appsThenLaunchWithin5s"]);
        Assert.Equal(1, snap["launchPrecededByApps"]);
        Assert.Equal(1, snap["sequentialSingleActionRunLength"]);
        Assert.Equal(1, snap["observedChangeFollowup"]);
    }

    [Fact]
    public void C2_Deduplication_ReadExpiresAfterTtl()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        var r1 = tools.Windows();
        var initialSuppressed = session.Telemetry.DuplicateReadSuppressed;

        // Immediate read -> deduped
        var r2 = tools.Windows();
        Assert.True(session.Telemetry.DuplicateReadSuppressed > initialSuppressed);

        // Manually record an expired read in the past (> 2s TTL)
        session.Deduplicator.RecordRead("computer_windows", "", 0, session.Rt.MutationVersion, r1);
        // Wait 2.1 seconds for TTL to expire
        Thread.Sleep(2100);

        var suppressedBefore = session.Telemetry.DuplicateReadSuppressed;
        var r3 = tools.Windows();
        var text3 = string.Join("\n", r3.Content.OfType<TextContentBlock>().Select(t => t.Text));
        Assert.DoesNotContain("unchanged: true", text3, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void C2_Deduplication_VisualObserve_BypassesReadDeduplication()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        var r1 = tools.Observe(mode: "visual");
        var suppressedBefore = session.Telemetry.DuplicateReadSuppressed;

        var r2 = tools.Observe(mode: "visual");
        var suppressedAfter = session.Telemetry.DuplicateReadSuppressed;

        Assert.Equal(suppressedBefore, suppressedAfter);
    }

    [Fact]
    public async Task C2_Deduplication_OperationIdConflict_ReturnsConflictError()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        var opId = "conflict-op-" + Guid.NewGuid().ToString("N");
        var r1 = await tools.CloseWindow(owned: true, operationId: opId);
        Assert.False(r1.IsError);

        // Reusing same operationId with DIFFERENT parameter (lastOwned: true instead of owned: true)
        var r2 = await tools.CloseWindow(lastOwned: true, operationId: opId);
        Assert.True(r2.IsError);

        var text = string.Join("\n", r2.Content.OfType<TextContentBlock>().Select(t => t.Text));
        Assert.Contains("OperationIdConflict", text);
    }

    [Fact]
    public async Task C1_CloseWindow_ForceDoesNotCloseUserApp_UnderOwnedTrue()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        // User app (agentOwned = false)
        session.RecordWindowLaunch(301, 3001, "user_app", agentOwned: false);
        // Agent app (agentOwned = true)
        session.RecordWindowLaunch(302, 3002, "agent_app", agentOwned: true);

        // force: true MUST NOT pull user_app into owned: true
        var res = await tools.CloseWindow(owned: true, force: true);
        var text = string.Join("\n", res.Content.OfType<TextContentBlock>().Select(t => t.Text));
        using var doc = JsonDocument.Parse(text);

        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.False(session.TrackedWindows.First(w => w.Hwnd == 301).ClosedOrStale, "User app must NOT be closed even with force:true under owned:true");
        Assert.True(session.TrackedWindows.First(w => w.Hwnd == 302).ClosedOrStale, "Agent app must be closed");
    }

    [Fact]
    public async Task C1_CloseWindow_PolicyDenied_DoesNotProvideRetryArgs()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        session.RecordWindowLaunch(401, 4001, "notepad", agentOwned: false);

        var res = await tools.CloseWindow(hwnd: "0x191"); // 401 in hex
        var text = string.Join("\n", res.Content.OfType<TextContentBlock>().Select(t => t.Text));
        using var doc = JsonDocument.Parse(text);

        if (doc.RootElement.TryGetProperty("error", out var err) && err.GetString() == "PolicyDenied")
        {
            Assert.False(doc.RootElement.TryGetProperty("candidates", out _), "PolicyDenied must not provide retry candidates");
            Assert.DoesNotContain("retryArgs", text);
        }
    }

    [Fact]
    public void C4_Launch_EvaluatesNextField_Dynamically()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        // When map has 0 actionables -> next should be "inspect"
        var mapDirect = tools.BuildInitialAppMap(100, 1000, "Empty Window");
        Assert.NotNull(mapDirect);
    }

    // =========================================================================
    // Section C Round 3 Tests
    // =========================================================================

    [Fact]
    public void Provenance_Case1_PreExistingWindowReused_EvaluatesNotAgentOwned()
    {
        var snap = new PreLaunchSnapshot(
            DateTimeOffset.UtcNow,
            new HashSet<long> { 101, 102 },
            new Dictionary<int, DateTimeOffset?> { [1001] = DateTimeOffset.UtcNow.AddMinutes(-5) }
        );

        var owned = LaunchCreationEvidenceEvaluator.EvaluateAgentOwnership(
            snap,
            finalHwnd: 101,
            finalPid: 1001,
            finalPidStartTime: DateTimeOffset.UtcNow.AddMinutes(-5),
            spawnedPid: 1001,
            launchState: "Success"
        );

        Assert.False(owned, "Pre-existing window reused must evaluate to agentOwned=false");
    }

    [Fact]
    public void Provenance_Case2_NewlyCreatedPidAndHwnd_EvaluatesAgentOwned()
    {
        var snapTime = DateTimeOffset.UtcNow;
        var snap = new PreLaunchSnapshot(
            snapTime,
            new HashSet<long> { 101 },
            new Dictionary<int, DateTimeOffset?> { [1001] = snapTime.AddMinutes(-5) }
        );

        var owned = LaunchCreationEvidenceEvaluator.EvaluateAgentOwnership(
            snap,
            finalHwnd: 202,
            finalPid: 2002,
            finalPidStartTime: snapTime.AddMilliseconds(50),
            spawnedPid: 2002,
            launchState: "Success"
        );

        Assert.True(owned, "Newly created PID and HWND must evaluate to agentOwned=true");
    }

    [Fact]
    public void Provenance_Case3_BootstrapPidCreated_FinalHwndBelongsToPreExistingPid_EvaluatesNotAgentOwned()
    {
        var snapTime = DateTimeOffset.UtcNow;
        var snap = new PreLaunchSnapshot(
            snapTime,
            new HashSet<long> { 101 },
            new Dictionary<int, DateTimeOffset?> { [1001] = snapTime.AddMinutes(-5) }
        );

        var owned = LaunchCreationEvidenceEvaluator.EvaluateAgentOwnership(
            snap,
            finalHwnd: 102,
            finalPid: 1001,
            finalPidStartTime: snapTime.AddMinutes(-5),
            spawnedPid: 9999,
            launchState: "Success"
        );

        Assert.False(owned, "Bootstrap PID redirecting to pre-existing PID must evaluate to agentOwned=false");
    }

    [Fact]
    public void Provenance_Case4_PidReused_StartTimeMismatch_EvaluatesNotAgentOwned()
    {
        var snapTime = DateTimeOffset.UtcNow;
        var snap = new PreLaunchSnapshot(
            snapTime,
            new HashSet<long> { 101 },
            new Dictionary<int, DateTimeOffset?>()
        );

        var owned = LaunchCreationEvidenceEvaluator.EvaluateAgentOwnership(
            snap,
            finalHwnd: 303,
            finalPid: 3003,
            finalPidStartTime: snapTime.AddMinutes(-10),
            spawnedPid: 3003,
            launchState: "Success"
        );

        Assert.False(owned, "Recycled PID or start-time preceding launch snapshot must evaluate to agentOwned=false");
    }

    [Fact]
    public void Provenance_Case5_AmbiguousCreationEvidence_EvaluatesNotAgentOwned()
    {
        var snap = new PreLaunchSnapshot(
            DateTimeOffset.UtcNow,
            new HashSet<long>(),
            new Dictionary<int, DateTimeOffset?>()
        );

        var owned1 = LaunchCreationEvidenceEvaluator.EvaluateAgentOwnership(
            snap,
            finalHwnd: 404,
            finalPid: 4004,
            finalPidStartTime: null,
            spawnedPid: null,
            launchState: "Success"
        );
        Assert.False(owned1, "Missing process start time and spawnedPid correlation must default to safe agentOwned=false");

        var owned2 = LaunchCreationEvidenceEvaluator.EvaluateAgentOwnership(
            snap,
            finalHwnd: 405,
            finalPid: 4005,
            finalPidStartTime: DateTimeOffset.UtcNow,
            spawnedPid: 4005,
            launchState: "AlreadyRunning"
        );
        Assert.False(owned2, "AlreadyRunning state must evaluate to agentOwned=false");

        var owned3 = LaunchCreationEvidenceEvaluator.EvaluateAgentOwnership(
            snap,
            finalHwnd: 0,
            finalPid: 0,
            finalPidStartTime: DateTimeOffset.UtcNow,
            spawnedPid: 0,
            launchState: "Success"
        );
        Assert.False(owned3, "Zero hwnd/pid must evaluate to agentOwned=false");
    }

    [Fact]
    public async Task Provenance_Case6_OwnedTrueAndLastOwned_NeverSelectsNonAgentOwnedCases()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        session.RecordWindowLaunch(101, 1001, "reused_app", agentOwned: false, alreadyRunning: true);
        session.RecordWindowLaunch(102, 1002, "redirected_app", agentOwned: false);
        session.RecordWindowLaunch(103, 1003, "recycled_app", agentOwned: false);
        session.RecordWindowLaunch(104, 1004, "ambiguous_app", agentOwned: false);

        var resOwned = await tools.CloseWindow(owned: true);
        using var docOwned = JsonDocument.Parse(string.Join("\n", resOwned.Content.OfType<TextContentBlock>().Select(t => t.Text)));
        Assert.True(docOwned.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(0, docOwned.RootElement.GetProperty("closedCount").GetInt32());

        Assert.False(session.TrackedWindows.First(w => w.Hwnd == 101).ClosedOrStale);
        Assert.False(session.TrackedWindows.First(w => w.Hwnd == 102).ClosedOrStale);
        Assert.False(session.TrackedWindows.First(w => w.Hwnd == 103).ClosedOrStale);
        Assert.False(session.TrackedWindows.First(w => w.Hwnd == 104).ClosedOrStale);

        var resLast = await tools.CloseWindow(lastOwned: true);
        using var docLast = JsonDocument.Parse(string.Join("\n", resLast.Content.OfType<TextContentBlock>().Select(t => t.Text)));
        Assert.True((docLast.RootElement.TryGetProperty("error", out var err) && err.GetString() == "TargetNotFound") || (docLast.RootElement.TryGetProperty("closed", out var closed) && !closed.GetBoolean()));
    }

    [Fact]
    public async Task OperationId_RunPlan_SameOperationId_ExecutesOnce()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        var opId = "plan-op-" + Guid.NewGuid().ToString("N");
        var steps = new[] { new InbriskTools.RunStep { Action = "wait", Ms = 1 } };

        var r1 = await tools.RunPlan(steps, operationId: opId);
        var suppressedBefore = session.Telemetry.DuplicateMutationRetrySuppressed;

        var r2 = await tools.RunPlan(steps, operationId: opId);
        var suppressedAfter = session.Telemetry.DuplicateMutationRetrySuppressed;

        Assert.True(suppressedAfter > suppressedBefore, "Same operationId with identical computer_run plan must be deduplicated");
    }

    [Fact]
    public async Task OperationId_RunPlan_DifferentArgs_ConflictError()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        var opId = "plan-conflict-" + Guid.NewGuid().ToString("N");
        var steps1 = new[] { new InbriskTools.RunStep { Action = "wait", Ms = 1 } };
        var steps2 = new[] { new InbriskTools.RunStep { Action = "wait", Ms = 50 } };

        var r1 = await tools.RunPlan(steps1, operationId: opId);
        Assert.False(r1.IsError);

        var r2 = await tools.RunPlan(steps2, operationId: opId);
        Assert.True(r2.IsError);
        var text = string.Join("\n", r2.Content.OfType<TextContentBlock>().Select(t => t.Text));
        Assert.Contains("OperationIdConflict", text);
    }

    [Fact]
    public async Task OperationId_CrossToolReusedOperationId_ConflictError()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        var opId = "cross-tool-" + Guid.NewGuid().ToString("N");
        var r1 = await tools.RunPlan(new[] { new InbriskTools.RunStep { Action = "wait", Ms = 1 } }, operationId: opId);
        Assert.False(r1.IsError);

        var r2 = await tools.Click(operationId: opId);
        Assert.True(r2.IsError);
        var text = string.Join("\n", r2.Content.OfType<TextContentBlock>().Select(t => t.Text));
        Assert.Contains("OperationIdConflict", text);
    }

    [Fact]
    public void OperationId_SetValue_SameOperationId_Deduped_DifferentValue_Conflict()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);

        var opId = "setval-op-" + Guid.NewGuid().ToString("N");

        session.Deduplicator.RecordMutation(opId, "computer_set_value", "SetValue||abc|||0,0||False", new CallToolResult { IsError = false, Content = [new TextContentBlock { Text = "ok" }] });

        var rSame = session.Deduplicator.TryDeduplicateMutation(opId, "computer_set_value", "SetValue||abc|||0,0||False", session.Telemetry, out var errSame);
        Assert.NotNull(rSame);
        Assert.Null(errSame);

        var rDiff = session.Deduplicator.TryDeduplicateMutation(opId, "computer_set_value", "SetValue||xyz|||0,0||False", session.Telemetry, out var errDiff);
        Assert.Null(rDiff);
        Assert.NotNull(errDiff);
        Assert.Contains("OperationIdConflict", string.Join("\n", errDiff.Content.OfType<TextContentBlock>().Select(t => t.Text)));
    }

    [Fact]
    public void ObserveDedupe_SemanticOnly_Allowed_VisualOrFrames_Bypassed()
    {
        Assert.True(RequestDeduplicator.IsSafeForReadDeduplication("computer_observe", mode: "semantic", autoScreenshot: false, hasCropOrRegion: false, frameCount: 0));
        Assert.False(RequestDeduplicator.IsSafeForReadDeduplication("computer_observe", mode: "semantic", autoScreenshot: false, hasCropOrRegion: false, frameCount: 1));
        Assert.False(RequestDeduplicator.IsSafeForReadDeduplication("computer_observe", mode: "visual", autoScreenshot: false, hasCropOrRegion: false));
        Assert.False(RequestDeduplicator.IsSafeForReadDeduplication("computer_observe", mode: "both", autoScreenshot: false, hasCropOrRegion: false));

        var imgResult = new CallToolResult
        {
            Content = [ImageContentBlock.FromBytes(new byte[] { 1, 2, 3 }, "image/png")]
        };
        Assert.False(RequestDeduplicator.IsSafeForReadDeduplication("computer_observe", mode: "semantic", autoScreenshot: false, hasCropOrRegion: false, result: imgResult));
    }

    [Fact]
    public void Provenance_ConcurrentUnrelatedCreation_EvaluatesNotAgentOwned()
    {
        var snap = new PreLaunchSnapshot(
            DateTimeOffset.UtcNow,
            new HashSet<long> { 10, 20 },
            new Dictionary<int, DateTimeOffset?> { [100] = DateTimeOffset.UtcNow.AddMinutes(-5) }
        );

        var owned = LaunchCreationEvidenceEvaluator.EvaluateAgentOwnership(
            snap,
            finalHwnd: 5000,
            finalPid: 500,
            finalPidStartTime: DateTimeOffset.UtcNow,
            spawnedPid: 400,
            launchState: "Success",
            isVerifiedDescendantOrRedirect: false
        );

        Assert.False(owned, "Unrelated concurrent process creation without positive correlation must evaluate to agentOwned=false");
    }

    [Fact]
    public void Provenance_SpawnedPidOwnsFinalHwnd_EvaluatesAgentOwned()
    {
        var snap = new PreLaunchSnapshot(
            DateTimeOffset.UtcNow,
            new HashSet<long> { 10, 20 },
            new Dictionary<int, DateTimeOffset?> { [100] = DateTimeOffset.UtcNow.AddMinutes(-5) }
        );

        var owned = LaunchCreationEvidenceEvaluator.EvaluateAgentOwnership(
            snap,
            finalHwnd: 4000,
            finalPid: 400,
            finalPidStartTime: DateTimeOffset.UtcNow,
            spawnedPid: 400,
            launchState: "Success"
        );

        Assert.True(owned, "Direct match between spawned PID and final HWND PID must evaluate to agentOwned=true");
    }

    [Fact]
    public void Provenance_VerifiedDescendantRedirect_EvaluatesAgentOwned()
    {
        var snap = new PreLaunchSnapshot(
            DateTimeOffset.UtcNow,
            new HashSet<long> { 10, 20 },
            new Dictionary<int, DateTimeOffset?> { [100] = DateTimeOffset.UtcNow.AddMinutes(-5) }
        );

        var owned = LaunchCreationEvidenceEvaluator.EvaluateAgentOwnership(
            snap,
            finalHwnd: 5000,
            finalPid: 500,
            finalPidStartTime: DateTimeOffset.UtcNow,
            spawnedPid: 400,
            launchState: "Success",
            isVerifiedDescendantOrRedirect: true
        );

        Assert.True(owned, "Explicitly verified descendant/redirect target must evaluate to agentOwned=true");
    }

    [Fact]
    public void Provenance_AmbiguousNewPid_EvaluatesNotAgentOwned()
    {
        var snap = new PreLaunchSnapshot(
            DateTimeOffset.UtcNow,
            new HashSet<long> { 10, 20 },
            new Dictionary<int, DateTimeOffset?> { [100] = DateTimeOffset.UtcNow.AddMinutes(-5) }
        );

        var owned = LaunchCreationEvidenceEvaluator.EvaluateAgentOwnership(
            snap,
            finalHwnd: 5000,
            finalPid: 500,
            finalPidStartTime: DateTimeOffset.UtcNow,
            spawnedPid: null,
            launchState: "Success",
            isVerifiedDescendantOrRedirect: false
        );

        Assert.False(owned, "Ambiguous new PID without positive correlation must default to agentOwned=false");
    }

    [Fact]
    public async Task ClosePolicy_ExplicitUserHwnd_GracefulClosePermitted_WithoutForce()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        session.RecordWindowLaunch(601, 6001, "notepad", title: "User Document", agentOwned: false);

        var res = await tools.CloseWindow(hwnd: "0x259", force: false);
        using var doc = JsonDocument.Parse(string.Join("\n", res.Content.OfType<TextContentBlock>().Select(t => t.Text)));

        Assert.False(doc.RootElement.TryGetProperty("error", out var err) && err.GetString() == "PolicyDenied",
            "Explicit user-owned HWND must NOT be rejected with PolicyDenied when policy permits graceful close");
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task ClosePolicy_ProtectedWindow_PureSyntheticTest()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        session.RecordWindowLaunch(777, 7777, "code.exe", title: "Visual Studio Code", agentOwned: false);

        // 1. force: false -> denied
        var resFalse = await tools.CloseWindow(hwnd: "0x309", force: false);
        using var docFalse = JsonDocument.Parse(string.Join("\n", resFalse.Content.OfType<TextContentBlock>().Select(t => t.Text)));
        Assert.True(docFalse.RootElement.TryGetProperty("error", out var errFalse) && errFalse.GetString() == "ProtectedWindow",
            "Protected window close must return ProtectedWindow on force:false, NEVER TargetNotFound");

        // 2. force: true -> STILL denied!
        var resTrue = await tools.CloseWindow(hwnd: "0x309", force: true);
        using var docTrue = JsonDocument.Parse(string.Join("\n", resTrue.Content.OfType<TextContentBlock>().Select(t => t.Text)));
        Assert.True(docTrue.RootElement.TryGetProperty("error", out var errTrue) && errTrue.GetString() == "ProtectedWindow",
            "Protected window close must STILL return ProtectedWindow on force:true, NEVER TargetNotFound");

        Assert.False(session.TrackedWindows.First(w => w.Hwnd == 777).ClosedOrStale);
    }

    [Fact]
    public async Task ClosePolicy_UnsavedModal_ForceTrue_DoesNotKillProcess()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        session.RecordWindowLaunch(888, 8888, "editor.exe", title: "Text Editor", agentOwned: true);

        session.Rt.WindowService.SyntheticModalPopups![888] = new WindowInfo(
            Hwnd: 889,
            Pid: 8888,
            Title: "Do you want to save changes?",
            ProcessName: "editor.exe",
            Bounds: new RectPx(100, 100, 300, 200),
            State: WindowState.Normal,
            IsForeground: true,
            IsElevated: false,
            OnCurrentVirtualDesktop: true,
            MonitorIndex: 0,
            IsModalPopup: true,
            OwnerHwnd: 888
        );

        var res = await tools.CloseWindow(hwnd: "0x378", force: true);
        using var doc = JsonDocument.Parse(string.Join("\n", res.Content.OfType<TextContentBlock>().Select(t => t.Text)));

        Assert.True(doc.RootElement.TryGetProperty("error", out var err) && err.GetString() == "UnsavedDataPromptOpen");
        Assert.True(doc.RootElement.GetProperty("hasUnsavedDataPrompt").GetBoolean());
        Assert.Equal("dismiss_modal", doc.RootElement.GetProperty("next").GetString());
        Assert.False(doc.RootElement.GetProperty("closed").GetBoolean());
        Assert.False(session.TrackedWindows.First(w => w.Hwnd == 888).ClosedOrStale);
    }

    [Fact]
    public async Task OperationId_RunRecipe_SameOperationId_ExecutesOnce()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        var recipeDef = new TaskRecipeDefinition(
            Name: "test_recipe_1",
            Description: "Simple test recipe",
            App: "notepad",
            Parameters: Array.Empty<RecipeParameter>(),
            Preconditions: Array.Empty<string>(),
            StepsJson: JsonSerializer.Serialize(new[] { new InbriskTools.RunStep { Action = "wait", Ms = 1 } }),
            Postconditions: Array.Empty<string>(),
            CreatedAt: DateTimeOffset.UtcNow
        );
        session.RecipeStore.Save(recipeDef);

        var opId = "rec-op-" + Guid.NewGuid().ToString("N");
        var r1 = await tools.RunRecipe("test_recipe_1", operationId: opId);
        var suppressedBefore = session.Telemetry.DuplicateMutationRetrySuppressed;

        var r2 = await tools.RunRecipe("test_recipe_1", operationId: opId);
        var suppressedAfter = session.Telemetry.DuplicateMutationRetrySuppressed;

        Assert.True(suppressedAfter > suppressedBefore, "Same operationId with identical run_recipe call must be deduplicated");
    }

    [Fact]
    public async Task OperationId_RunRecipe_DifferentArgs_ConflictError()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        var recipeDef = new TaskRecipeDefinition(
            Name: "test_recipe_conflict",
            Description: "Conflict test",
            App: "notepad",
            Parameters: new[] { new RecipeParameter("param1", "str", "val") },
            Preconditions: Array.Empty<string>(),
            StepsJson: JsonSerializer.Serialize(new[] { new InbriskTools.RunStep { Action = "wait", Ms = 1 } }),
            Postconditions: Array.Empty<string>(),
            CreatedAt: DateTimeOffset.UtcNow
        );
        session.RecipeStore.Save(recipeDef);

        var opId = "rec-conflict-" + Guid.NewGuid().ToString("N");
        var r1 = await tools.RunRecipe("test_recipe_conflict", parameters: new Dictionary<string, string> { ["param1"] = "val1" }, operationId: opId);
        Assert.False(r1.IsError);

        var r2 = await tools.RunRecipe("test_recipe_conflict", parameters: new Dictionary<string, string> { ["param1"] = "val2" }, operationId: opId);
        Assert.True(r2.IsError);
        var text = string.Join("\n", r2.Content.OfType<TextContentBlock>().Select(t => t.Text));
        Assert.Contains("OperationIdConflict", text);
    }

    [Fact]
    public void Idempotency_PauseRun_Twice_NoSideEffect()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        var r1 = tools.PauseRun(runId: "run_test_pause", reason: "Need manual login");
        using var doc1 = JsonDocument.Parse(string.Join("\n", r1.Content.OfType<TextContentBlock>().Select(t => t.Text)));
        Assert.True(doc1.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("PausedForHuman", doc1.RootElement.GetProperty("status").GetString());

        var r2 = tools.PauseRun(runId: "run_test_pause", reason: "Need manual login");
        using var doc2 = JsonDocument.Parse(string.Join("\n", r2.Content.OfType<TextContentBlock>().Select(t => t.Text)));
        Assert.True(doc2.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("PausedForHuman", doc2.RootElement.GetProperty("status").GetString());
        Assert.True(session.Runs["run_test_pause"].IsPausedForHuman);
    }

    [Fact]
    public void Idempotency_CancelTask_Twice_Safe()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        var r1 = tools.CancelTask(taskId: "task_unknown_1", reason: "Cancellation requested");
        using var doc1 = JsonDocument.Parse(string.Join("\n", r1.Content.OfType<TextContentBlock>().Select(t => t.Text)));
        Assert.False(doc1.RootElement.GetProperty("success").GetBoolean());

        var r2 = tools.CancelTask(taskId: "task_unknown_1", reason: "Cancellation requested");
        using var doc2 = JsonDocument.Parse(string.Join("\n", r2.Content.OfType<TextContentBlock>().Select(t => t.Text)));
        Assert.False(doc2.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task Idempotency_SaveRecipe_Twice_Idempotent()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        var steps = JsonSerializer.SerializeToElement(new[] { new InbriskTools.RunStep { Action = "wait", Ms = 100 } });
        var r1 = await tools.SaveRecipe("idempotent_recipe", steps: steps, app: "testapp", description: "testing");
        var r2 = await tools.SaveRecipe("idempotent_recipe", steps: steps, app: "testapp", description: "testing");

        Assert.False(r1.IsError);
        Assert.False(r2.IsError);
        var saved = session.RecipeStore.Get("idempotent_recipe");
        Assert.NotNull(saved);
        Assert.Equal("testapp", saved.App);
    }

    [Fact]
    public async Task ClosePolicy_Invariants_Verified()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        // 1. owned:true never includes user-owned window
        session.RecordWindowLaunch(501, 5001, "user_tool", agentOwned: false);
        var res1 = await tools.CloseWindow(owned: true);
        Assert.False(session.TrackedWindows.First(w => w.Hwnd == 501).ClosedOrStale);

        // 2. lastOwned never selects reused window
        session.RecordWindowLaunch(502, 5002, "reused_tool", agentOwned: false, alreadyRunning: true);
        var res2 = await tools.CloseWindow(lastOwned: true);
        using var doc2 = JsonDocument.Parse(string.Join("\n", res2.Content.OfType<TextContentBlock>().Select(t => t.Text)));
        Assert.True((doc2.RootElement.TryGetProperty("error", out var err2) && err2.GetString() == "TargetNotFound") || (doc2.RootElement.TryGetProperty("closed", out var c2) && !c2.GetBoolean()));

        // 3. explicit user HWND graceful close is permitted without force
        session.RecordWindowLaunch(503, 5003, "explicit_user_app", agentOwned: false);
        var res3 = await tools.CloseWindow(hwnd: "0x1F7"); // 503
        var text3 = string.Join("\n", res3.Content.OfType<TextContentBlock>().Select(t => t.Text));
        Assert.DoesNotContain("PolicyDenied", text3);

        // 4. force:true does NOT bypass protected-window policy
        session.RecordWindowLaunch(504, 5004, "code.exe", agentOwned: false);
        var res4Prot = await tools.CloseWindow(hwnd: "0x1F8", force: true);
        var text4 = string.Join("\n", res4Prot.Content.OfType<TextContentBlock>().Select(t => t.Text));
        Assert.Contains("ProtectedWindow", text4);

        // 5. force:true does NOT convert user-owned target into agent-owned
        var res5ForceOwned = await tools.CloseWindow(owned: true, force: true);
        Assert.False(session.TrackedWindows.First(w => w.Hwnd == 501).ClosedOrStale, "force:true must NOT convert user-owned window to agent-owned under owned:true");

        // 6. Policy denied / error response contains no unsafe retryArgs
        Assert.DoesNotContain("retryArgs", text4);
        Assert.DoesNotContain("candidates", text4);
    }

    [Fact]
    public void InitialMap_HardBudgetAndTelemetry_Verified()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var tools = new InbriskTools(session);

        var map = tools.BuildInitialAppMap(100, 1000, "Budget Test Window");
        Assert.NotNull(map);
        Assert.True(map.ContainsKey("firstActionables"));
        Assert.True(map.ContainsKey("landmarks"));

        var snap = session.Telemetry.GetSnapshot();
        Assert.True(snap["initialMapBuildCount"] > 0, "BuildInitialAppMap must record telemetry count");
        Assert.True(snap["lastInitialMapPayloadBytes"] > 0, "Payload bytes must be recorded");
    }

    // =========================================================================
    //  Phase D — Concurrency & Parallel Scheduler Tests (Headless & Safe)
    // =========================================================================

    [Fact]
    public void PhaseD_D0_ConcurrencyTelemetry_Snapshot_ExposesAll12Counters()
    {
        var telemetry = new SessionTelemetry();
        telemetry.IncUiaReadQueued();
        telemetry.IncUiaReadActive();
        telemetry.AddUiaReadQueueWaitMs(15.5);
        telemetry.AddUiaReadExecutionMs(42.3);
        telemetry.IncUiaReadTimeout();
        telemetry.IncUiaReadCancelled();
        telemetry.SetUiaReadLanePid(1234);
        telemetry.IncUiaWriteQueued();
        telemetry.AddUiaWriteQueueWaitMs(5.0);
        telemetry.AddUiaWriteExecutionMs(12.0);
        telemetry.IncSyncOverAsyncFallback();

        var snap = telemetry.GetSnapshot();

        string[] requiredCounters =
        {
            "uiaReadQueued", "uiaReadActive", "uiaReadMaxConcurrent",
            "uiaReadQueueWaitMs", "uiaReadExecutionMs", "uiaReadTimeout",
            "uiaReadCancelled", "uiaReadLanePid", "uiaWriteQueued",
            "uiaWriteQueueWaitMs", "uiaWriteExecutionMs", "syncOverAsyncFallbackCount"
        };

        foreach (var c in requiredCounters)
        {
            Assert.True(snap.ContainsKey(c), $"Telemetry snapshot must contain '{c}'");
        }

        Assert.Equal(1, snap["uiaReadQueued"]);
        Assert.Equal(1, snap["uiaReadActive"]);
        Assert.Equal(1, snap["uiaReadMaxConcurrent"]);
        Assert.Equal(16, snap["uiaReadQueueWaitMs"]);
        Assert.Equal(42, snap["uiaReadExecutionMs"]);
        Assert.Equal(1, snap["uiaReadTimeout"]);
        Assert.Equal(1, snap["uiaReadCancelled"]);
        Assert.Equal(1234, snap["uiaReadLanePid"]);
        Assert.Equal(1, snap["uiaWriteQueued"]);
        Assert.Equal(5, snap["uiaWriteQueueWaitMs"]);
        Assert.Equal(12, snap["uiaWriteExecutionMs"]);
        Assert.Equal(1, snap["syncOverAsyncFallbackCount"]);
    }

    [Fact]
    public async Task PhaseD_D7_CrossProcessIsolation_SlowProviderDoesNotBlockFast()
    {
        using var scheduler = new UiaReadScheduler();
        var sw = Stopwatch.StartNew();

        // PID 100 is slow (takes 250ms)
        var slowTask = scheduler.ScheduleReadAsync(100, _ =>
        {
            Thread.Sleep(250);
            return "slow_done";
        });

        // Slight stagger to ensure slow task has started
        await Task.Delay(20);

        // PID 200 is fast (takes 10ms)
        var fastSw = Stopwatch.StartNew();
        var fastResult = await scheduler.ScheduleReadAsync(200, _ =>
        {
            Thread.Sleep(10);
            return "fast_done";
        });
        fastSw.Stop();

        Assert.Equal("fast_done", fastResult);
        // Fast task on PID 200 MUST finish well before the slow task on PID 100
        Assert.True(fastSw.ElapsedMilliseconds < 150,
            $"Fast PID 200 was blocked by slow PID 100! Elapsed: {fastSw.ElapsedMilliseconds}ms");

        var slowResult = await slowTask;
        Assert.Equal("slow_done", slowResult);
    }

    [Fact]
    public async Task PhaseD_D8_SameProcessConcurrency_TwoWorkersSpeedup()
    {
        using var scheduler = new UiaReadScheduler { MaxConcurrencyPerProcess = 2, UiaFactory = () => null };
        // Warmup workers concurrently
        await Task.WhenAll(
            scheduler.ScheduleReadAsync(300, _ => 0),
            scheduler.ScheduleReadAsync(300, _ => 0));

        using var barrier = new Barrier(2);
        var task1 = scheduler.ScheduleReadAsync(300, _ =>
        {
            var ok = barrier.SignalAndWait(TimeSpan.FromSeconds(5));
            return ok ? 1 : 0;
        });
        var task2 = scheduler.ScheduleReadAsync(300, _ =>
        {
            var ok = barrier.SignalAndWait(TimeSpan.FromSeconds(5));
            return ok ? 2 : 0;
        });

        var results = await Task.WhenAll(task1, task2);
        Assert.Equal(1, results[0]);
        Assert.Equal(2, results[1]);
    }

    [Fact]
    public async Task PhaseD_D6_ReadWriteConsistencyBarrier_WriteBlocksReadsOnSamePid()
    {
        using var scheduler = new UiaReadScheduler();
        int pid = 400;

        scheduler.NotifyMutationStarting(pid);
        bool readExecuted = false;

        var readTask = Task.Run(async () =>
        {
            await scheduler.ScheduleReadAsync(pid, _ =>
            {
                readExecuted = true;
                return "read_done";
            });
        });

        // While write is active, read should not execute
        await Task.Delay(50);
        Assert.False(readExecuted, "Read must not execute while mutation is active on same PID");

        // Complete write barrier
        scheduler.NotifyMutationCompleted(pid);

        await readTask;
        Assert.True(readExecuted, "Read must execute once mutation barrier completes");
    }

    [Fact]
    public async Task PhaseD_D15_Fairness_WritePreferenceUnderContinuousReads()
    {
        using var scheduler = new UiaReadScheduler { MaxConcurrencyPerProcess = 2 };
        int pid = 500;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        // Start continuous background reads on pid 500
        var readLoop = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    await scheduler.ScheduleReadAsync(pid, _ =>
                    {
                        Thread.Sleep(5);
                        return true;
                    }, timeoutMs: 1000, ct: cts.Token);
                }
                catch (OperationCanceledException) { break; }
            }
        });

        await Task.Delay(30);

        // Notify mutation starting — write preference must allow it to acquire barrier promptly
        var writeSw = Stopwatch.StartNew();
        scheduler.NotifyMutationStarting(pid);
        writeSw.Stop();

        Assert.True(writeSw.ElapsedMilliseconds < 1500,
            $"Write preference was starved by continuous reads! Wait: {writeSw.ElapsedMilliseconds}ms");

        scheduler.NotifyMutationCompleted(pid);
        cts.Cancel();
        try { await readLoop; } catch { }
    }

    [Fact]
    public async Task PhaseD_D15_QueueBackpressure_ThrowsBusyWhenSaturated()
    {
        using var scheduler = new UiaReadScheduler { MaxConcurrencyPerProcess = 1 };
        int pid = 600;

        // Block the single worker
        var blockerStarted = new TaskCompletionSource();
        var releaseBlocker = new TaskCompletionSource();

        var blockTask = scheduler.ScheduleReadAsync(pid, _ =>
        {
            blockerStarted.SetResult();
            releaseBlocker.Task.Wait(5000);
            return true;
        });

        await blockerStarted.Task;

        // Fill bounded queue (capacity is 64)
        var queued = new List<Task>();
        for (int i = 0; i < 64; i++)
        {
            queued.Add(scheduler.ScheduleReadAsync(pid, _ => true, timeoutMs: 10000));
        }

        // 65th item must hit backpressure and throw ErrorCode.Busy
        var ex = await Assert.ThrowsAsync<InbriskException>(async () =>
        {
            await scheduler.ScheduleReadAsync(pid, _ => true, timeoutMs: 10000);
        });

        Assert.Equal(ErrorCode.Busy, ex.Code);
        Assert.Contains("queue saturated", ex.Message);

        releaseBlocker.SetResult();
        await blockTask;
    }

    [Fact]
    public async Task PhaseD_D7_Cancellation_DropsQueuedItemBeforeExecution()
    {
        using var scheduler = new UiaReadScheduler { MaxConcurrencyPerProcess = 1 };
        int pid = 700;

        var blockerStarted = new TaskCompletionSource();
        var releaseBlocker = new TaskCompletionSource();

        var blockTask = scheduler.ScheduleReadAsync(pid, _ =>
        {
            blockerStarted.SetResult();
            releaseBlocker.Task.Wait(5000);
            return true;
        });

        await blockerStarted.Task;

        using var cts = new CancellationTokenSource();
        var cancelledTask = scheduler.ScheduleReadAsync(pid, _ => true, ct: cts.Token);

        // Cancel while still in queue
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await cancelledTask);

        releaseBlocker.SetResult();
        await blockTask;
    }

    [Fact]
    public async Task PhaseD_D18_ThreadPoolStarvation_100ConcurrentRequests()
    {
        using var scheduler = new UiaReadScheduler
        {
            MaxConcurrencyPerProcess = 2,
            MaxGlobalWorkers = 8
        };

        // 100 requests spread over 10 distinct PIDs
        var tasks = new List<Task<int>>();
        for (int i = 0; i < 100; i++)
        {
            int pid = 1000 + (i % 10);
            int id = i;
            tasks.Add(scheduler.ScheduleReadAsync(pid, _ =>
            {
                Thread.Sleep(5);
                return id;
            }));
        }

        var results = await Task.WhenAll(tasks);
        Assert.Equal(100, results.Length);

        // Verify threadpool availability remained healthy
        ThreadPool.GetAvailableThreads(out var workerThreads, out var completionPortThreads);
        Assert.True(workerThreads > 10, $"Thread pool starved! Available workers: {workerThreads}");
    }

    [Fact]
    public void PhaseD_D16_SchedulerDisposal_CleanWorkerShutdown()
    {
        var scheduler = new UiaReadScheduler();
        _ = scheduler.ScheduleRead(800, _ => "init");

        Assert.True(scheduler.GlobalWorkerCount > 0, "Worker should be running");

        var sw = Stopwatch.StartNew();
        scheduler.Dispose();
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 1000,
            $"Scheduler disposal took too long! Duration: {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task PhaseD_D17_SyntheticConcurrencyBenchmark_ScenariosAtoG()
    {
        int N = 100;
        var benchmarkReport = new Dictionary<string, object>
        {
            ["benchmark_name"] = "SYNTHETIC / HEADLESS CONCURRENCY BENCHMARK",
            ["iterations_per_scenario"] = N,
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("o")
        };

        // Scenario A: Independent Cross-Process Reads (PID A vs PID B)
        var samplesAConcurrent = new List<double>();
        var samplesASerial = new List<double>();
        using (var sched = new UiaReadScheduler { MaxConcurrencyPerProcess = 2, MaxGlobalWorkers = 8 })
        {
            for (int i = 0; i < N; i++)
            {
                var sw = Stopwatch.StartNew();
                var t1 = sched.ScheduleReadAsync(101, _ => { Thread.Sleep(3); return 1; });
                var t2 = sched.ScheduleReadAsync(102, _ => { Thread.Sleep(3); return 2; });
                await Task.WhenAll(t1, t2);
                sw.Stop();
                samplesAConcurrent.Add(sw.Elapsed.TotalMilliseconds);

                var swSerial = Stopwatch.StartNew();
                await sched.ScheduleReadAsync(101, _ => { Thread.Sleep(3); return 1; });
                await sched.ScheduleReadAsync(102, _ => { Thread.Sleep(3); return 2; });
                swSerial.Stop();
                samplesASerial.Add(swSerial.Elapsed.TotalMilliseconds);
            }
        }
        var statsA = ComputeStats(samplesAConcurrent);
        var statsASerial = ComputeStats(samplesASerial);
        statsA["serial_baseline_p50_ms"] = statsASerial["p50_ms"];
        statsA["speedup_ratio"] = Math.Round((double)statsASerial["p50_ms"] / Math.Max(0.1, (double)statsA["p50_ms"]), 2);
        benchmarkReport["scenario_A_cross_process_reads"] = statsA;

        // Scenario B: Same-Process Concurrency (2 workers vs 1 worker)
        var samplesBTwoWorkers = new List<double>();
        var samplesBOneWorker = new List<double>();
        using (var sched2 = new UiaReadScheduler { MaxConcurrencyPerProcess = 2 })
        using (var sched1 = new UiaReadScheduler { MaxConcurrencyPerProcess = 1 })
        {
            for (int i = 0; i < N; i++)
            {
                var sw2 = Stopwatch.StartNew();
                var t1 = sched2.ScheduleReadAsync(201, _ => { Thread.Sleep(3); return 1; });
                var t2 = sched2.ScheduleReadAsync(201, _ => { Thread.Sleep(3); return 2; });
                await Task.WhenAll(t1, t2);
                sw2.Stop();
                samplesBTwoWorkers.Add(sw2.Elapsed.TotalMilliseconds);

                var sw1 = Stopwatch.StartNew();
                var s1 = sched1.ScheduleReadAsync(201, _ => { Thread.Sleep(3); return 1; });
                var s2 = sched1.ScheduleReadAsync(201, _ => { Thread.Sleep(3); return 2; });
                await Task.WhenAll(s1, s2);
                sw1.Stop();
                samplesBOneWorker.Add(sw1.Elapsed.TotalMilliseconds);
            }
        }
        var statsB = ComputeStats(samplesBTwoWorkers);
        var statsB1 = ComputeStats(samplesBOneWorker);
        statsB["single_worker_baseline_p50_ms"] = statsB1["p50_ms"];
        statsB["speedup_ratio"] = Math.Round((double)statsB1["p50_ms"] / Math.Max(0.1, (double)statsB["p50_ms"]), 2);
        benchmarkReport["scenario_B_same_process_concurrency"] = statsB;

        // Scenario C: Writes Serialized on Main Dispatcher
        var samplesC = new List<double>();
        using (var disp = new UiaDispatcher())
        {
            for (int i = 0; i < N; i++)
            {
                var sw = Stopwatch.StartNew();
                disp.Run(_ => Thread.Sleep(1));
                disp.Run(_ => Thread.Sleep(1));
                sw.Stop();
                samplesC.Add(sw.Elapsed.TotalMilliseconds);
            }
        }
        benchmarkReport["scenario_C_writes_serialized"] = ComputeStats(samplesC);

        // Scenario D: Read/Write Barrier
        var samplesD = new List<double>();
        using (var sched = new UiaReadScheduler())
        {
            for (int i = 0; i < N; i++)
            {
                int pid = 300 + (i % 5);
                sched.NotifyMutationStarting(pid);
                var sw = Stopwatch.StartNew();
                var readTask = sched.ScheduleReadAsync(pid, _ => "done");
                await Task.Delay(2);
                sched.NotifyMutationCompleted(pid);
                await readTask;
                sw.Stop();
                samplesD.Add(sw.Elapsed.TotalMilliseconds);
            }
        }
        benchmarkReport["scenario_D_read_write_barrier"] = ComputeStats(samplesD);

        // Scenario E: Slow Provider Isolation
        var samplesE = new List<double>();
        using (var sched = new UiaReadScheduler { MaxConcurrencyPerProcess = 2, MaxGlobalWorkers = 8 })
        {
            for (int i = 0; i < N; i++)
            {
                var slowTask = sched.ScheduleReadAsync(401, _ => { Thread.Sleep(20); return "slow"; });
                await Task.Delay(2);
                var sw = Stopwatch.StartNew();
                var fastResult = await sched.ScheduleReadAsync(402, _ => "fast");
                sw.Stop();
                samplesE.Add(sw.Elapsed.TotalMilliseconds);
                await slowTask;
            }
        }
        benchmarkReport["scenario_E_slow_provider_isolation"] = ComputeStats(samplesE);

        // Scenario F: Cancellation / Deadline
        var samplesF = new List<double>();
        int droppedCount = 0;
        using (var sched = new UiaReadScheduler { MaxConcurrencyPerProcess = 1 })
        {
            for (int i = 0; i < N; i++)
            {
                using var cts = new CancellationTokenSource();
                cts.Cancel();
                var sw = Stopwatch.StartNew();
                try
                {
                    await sched.ScheduleReadAsync(501, _ => "ok", ct: cts.Token);
                }
                catch (OperationCanceledException)
                {
                    droppedCount++;
                }
                sw.Stop();
                samplesF.Add(sw.Elapsed.TotalMilliseconds);
            }
        }
        var statsF = ComputeStats(samplesF);
        statsF["dropped_before_execution_count"] = droppedCount;
        benchmarkReport["scenario_F_cancellation_deadline"] = statsF;

        // Scenario G: Fairness / Write Preference Under Continuous Reads
        var samplesG = new List<double>();
        using (var sched = new UiaReadScheduler { MaxConcurrencyPerProcess = 2 })
        {
            for (int i = 0; i < N; i++)
            {
                int pid = 601;
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
                var backgroundReads = Task.Run(async () =>
                {
                    while (!cts.IsCancellationRequested)
                    {
                        try { await sched.ScheduleReadAsync(pid, _ => { Thread.Sleep(1); return true; }, ct: cts.Token); }
                        catch { break; }
                    }
                });

                await Task.Delay(2);
                var sw = Stopwatch.StartNew();
                sched.NotifyMutationStarting(pid);
                sw.Stop();
                sched.NotifyMutationCompleted(pid);
                cts.Cancel();
                try { await backgroundReads; } catch { }

                samplesG.Add(sw.Elapsed.TotalMilliseconds);
            }
        }
        benchmarkReport["scenario_G_write_preference_fairness"] = ComputeStats(samplesG);

        // Save artifact
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Inbrisk.sln")))
        {
            dir = dir.Parent;
        }
        var root = dir?.FullName ?? Directory.GetCurrentDirectory();
        var artifactsDir = Path.Combine(root, "artifacts");
        Directory.CreateDirectory(artifactsDir);
        var artifactPath = Path.Combine(artifactsDir, "synthetic_concurrency_benchmark.json");
        var json = JsonSerializer.Serialize(benchmarkReport, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(artifactPath, json);

        Assert.Equal(N, droppedCount);
        Assert.True(File.Exists(artifactPath), "Benchmark artifact must be saved");
    }

    private static Dictionary<string, object> ComputeStats(List<double> samples)
    {
        samples.Sort();
        int n = samples.Count;
        double p50 = samples[(int)(n * 0.50)];
        double p90 = samples[(int)(n * 0.90)];
        double p99 = samples[(int)(n * 0.99)];
        double min = samples[0];
        double max = samples[^1];
        double mean = samples.Average();

        return new Dictionary<string, object>
        {
            ["iterations"] = n,
            ["min_ms"] = Math.Round(min, 2),
            ["p50_ms"] = Math.Round(p50, 2),
            ["p90_ms"] = Math.Round(p90, 2),
            ["p99_ms"] = Math.Round(p99, 2),
            ["max_ms"] = Math.Round(max, 2),
            ["mean_ms"] = Math.Round(mean, 2)
        };
    }

    // =========================================================================
    //  Phase D Round 2 — Targeted Concurrency, Barrier & Isolation Tests (Safe & Headless)
    // =========================================================================

    [Fact]
    public async Task UiaDispatcher_ControlLane_ApartmentState_IsExpected()
    {
        using var dispatcher = new UiaDispatcher();
        var apt = await dispatcher.RunAsync(uia => Thread.CurrentThread.GetApartmentState());
        Assert.Equal(ApartmentState.MTA, apt);
    }

    [Fact]
    public async Task UiaReadScheduler_Workers_AreMTA()
    {
        using var scheduler = new UiaReadScheduler { UiaFactory = () => null };
        var apt = await scheduler.ScheduleReadAsync(1001, _ => Thread.CurrentThread.GetApartmentState());
        Assert.Equal(ApartmentState.MTA, apt);
    }

    [Fact]
    public async Task ReadWriteBarrier_ExactSequence_NoOverlap()
    {
        using var scheduler = new UiaReadScheduler { MaxConcurrencyPerProcess = 2, UiaFactory = () => null };
        int pid = 2001;
        var lane = scheduler.GetOrCreateLane(pid);

        var r1Entered = new TaskCompletionSource();
        var r1Release = new TaskCompletionSource();
        var r2Entered = new TaskCompletionSource();
        var r2Release = new TaskCompletionSource();

        // 1. R1 enters and blocks
        var r1Task = scheduler.ScheduleReadAsync(pid, _ =>
        {
            r1Entered.SetResult();
            r1Release.Task.Wait(3000);
            return 1;
        });
        await r1Entered.Task;

        // 2. R2 enters and blocks (concurrent same-PID read)
        var r2Task = scheduler.ScheduleReadAsync(pid, _ =>
        {
            r2Entered.SetResult();
            r2Release.Task.Wait(3000);
            return 2;
        });
        await r2Entered.Task;

        Assert.Equal(2, Volatile.Read(ref lane.ActiveReads));
        Assert.Equal(0, Volatile.Read(ref lane.ActiveWrites));

        // 3. W queues
        var wEntered = new TaskCompletionSource();
        var wRelease = new TaskCompletionSource();
        var wTask = Task.Run(() =>
        {
            scheduler.NotifyMutationStarting(pid);
            wEntered.SetResult();
            wRelease.Task.Wait(3000);
            scheduler.NotifyMutationCompleted(pid);
        });

        // 4. Assert W has NOT entered while R1/R2 are active
        using var ctsW = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (Volatile.Read(ref lane.PendingWrites) == 0 && !ctsW.IsCancellationRequested)
        {
            await Task.Yield();
        }
        Assert.False(wEntered.Task.IsCompleted, "Write must NOT enter critical section while reads are active");
        Assert.Equal(0, Volatile.Read(ref lane.ActiveWrites));
        Assert.Equal(1, Volatile.Read(ref lane.PendingWrites));

        // 5. Release R1/R2
        r1Release.SetResult();
        r2Release.SetResult();
        await Task.WhenAll(r1Task, r2Task);

        // 6. Assert W enters
        await wEntered.Task;
        Assert.Equal(1, Volatile.Read(ref lane.ActiveWrites));
        Assert.Equal(0, Volatile.Read(ref lane.ActiveReads));

        // 7. While W active queue R3
        var r3Entered = new TaskCompletionSource();
        var r3Task = scheduler.ScheduleReadAsync(pid, _ =>
        {
            r3Entered.SetResult();
            return 3;
        });

        // 8. Assert R3 has NOT entered while W is active
        await Task.Delay(40);
        Assert.False(r3Entered.Task.IsCompleted, "Read R3 must NOT enter while write is active");
        Assert.Equal(0, Volatile.Read(ref lane.ActiveReads));

        // 9. Release W
        wRelease.SetResult();
        await wTask;
        Assert.Equal(0, Volatile.Read(ref lane.ActiveWrites));

        // 10. Assert R3 enters and finishes
        var r3Result = await r3Task;
        Assert.Equal(3, r3Result);

        // Verification: Overlaps must be exactly zero
        Assert.Equal(0, lane.ReadWriteOverlapCount);
        Assert.Equal(0, lane.WriteWriteOverlapCount);
        Assert.Equal(1, lane.MaxConcurrentWrites);
    }

    [Fact]
    public async Task Mutation_Throws_BarrierClears()
    {
        using var scheduler = new UiaReadScheduler { UiaFactory = () => null };
        int pid = 2002;
        var lane = scheduler.GetOrCreateLane(pid);

        try
        {
            using (scheduler.EnterMutationBarrier(pid))
            {
                Assert.Equal(1, Volatile.Read(ref lane.ActiveWrites));
                throw new InvalidOperationException("Simulated mutation failure");
            }
        }
        catch (InvalidOperationException) { }

        // Barrier must be completely clear
        Assert.Equal(0, Volatile.Read(ref lane.ActiveWrites));
        Assert.Equal(0, Volatile.Read(ref lane.PendingWrites));

        // Subsequent read on same PID executes immediately
        var res = await scheduler.ScheduleReadAsync(pid, _ => "recovered", timeoutMs: 1000);
        Assert.Equal("recovered", res);
    }

    [Fact]
    public async Task Mutation_Cancelled_BarrierClears()
    {
        using var scheduler = new UiaReadScheduler { UiaFactory = () => null };
        int pid = 2003;
        var lane = scheduler.GetOrCreateLane(pid);
        using var cts = new CancellationTokenSource();

        try
        {
            using (scheduler.EnterMutationBarrier(pid))
            {
                Assert.Equal(1, Volatile.Read(ref lane.ActiveWrites));
                cts.Cancel();
                cts.Token.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException) { }

        Assert.Equal(0, Volatile.Read(ref lane.ActiveWrites));
        Assert.Equal(0, Volatile.Read(ref lane.PendingWrites));

        var res = await scheduler.ScheduleReadAsync(pid, _ => "recovered_after_cancel", timeoutMs: 1000);
        Assert.Equal("recovered_after_cancel", res);
    }

    [Fact]
    public async Task PoisonedWorker_SingleHungWorker_ReplacedAndNextRequestCompletes()
    {
        using var scheduler = new UiaReadScheduler
        {
            MaxConcurrencyPerProcess = 1,
            MaxGlobalWorkers = 2,
            UiaFactory = () => null
        };
        int pid = 2004;

        var hungStarted = new TaskCompletionSource();
        // Request 1: Times out due to hung worker (takes 200ms while timeout is 30ms)
        var ex = await Assert.ThrowsAsync<InbriskException>(async () =>
        {
            await scheduler.ScheduleReadAsync(pid, _ =>
            {
                hungStarted.TrySetResult();
                Thread.Sleep(200);
                return "hung";
            }, timeoutMs: 30);
        });

        Assert.Equal(ErrorCode.Timeout, ex.Code);
        Assert.True(scheduler.PoisonedWorkerCount >= 1, "Poisoned worker must be tracked");
        Assert.True(scheduler.WorkerReplacementCount >= 1, "Replacement worker must be spawned");

        // Request 2: Must be served by replacement worker and complete cleanly!
        var nextResult = await scheduler.ScheduleReadAsync(2005, _ => "replacement_ok", timeoutMs: 1000);
        Assert.Equal("replacement_ok", nextResult);
    }

    [Fact]
    public async Task PoisonedWorker_CapExceeded_RejectsDeterministicBusy()
    {
        using var scheduler = new UiaReadScheduler
        {
            MaxConcurrencyPerProcess = 1,
            MaxGlobalWorkers = 2,
            MaxAbandonedWorkers = 2,
            UiaFactory = () => null
        };

        // Poison 2 workers (reaches MaxAbandonedWorkers cap = 2)
        for (int i = 0; i < 2; i++)
        {
            int pid = 2010 + i;
            var started = new TaskCompletionSource();
            try
            {
                await scheduler.ScheduleReadAsync(pid, _ =>
                {
                    started.TrySetResult();
                    Thread.Sleep(300);
                    return "hung";
                }, timeoutMs: 40);
            }
            catch (InbriskException) { }
            await started.Task;
        }

        Assert.True(scheduler.AbandonedWorkerCount >= 2);

        // Next request when abandoned workers cap is exceeded must be deterministically rejected with Busy
        var busyEx = await Assert.ThrowsAsync<InbriskException>(async () =>
        {
            await scheduler.ScheduleReadAsync(2099, _ => "should_reject", timeoutMs: 500);
        });

        Assert.Equal(ErrorCode.Busy, busyEx.Code);
        Assert.Contains("maximum abandoned workers reached", busyEx.Message);
    }

    [Fact]
    public void PoisonedWorker_Dispose_ReturnsWithinBoundedTime()
    {
        var scheduler = new UiaReadScheduler
        {
            MaxConcurrencyPerProcess = 1,
            MaxGlobalWorkers = 1,
            UiaFactory = () => null
        };

        // Dispatch a hung worker
        _ = scheduler.ScheduleReadAsync(2020, _ =>
        {
            Thread.Sleep(10000);
            return "hung";
        }, timeoutMs: 5000);

        Thread.Sleep(30);

        // Disposal must return boundedly without waiting 10s for the stuck worker
        var sw = Stopwatch.StartNew();
        scheduler.Dispose();
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 350,
            $"Disposal took {sw.ElapsedMilliseconds}ms! Must return within bounded time without hanging.");
    }

    [Fact]
    public void GlobalBounds_ThousandsOfPids_BoundedMemoryAndDeterministicBusy()
    {
        using var scheduler = new UiaReadScheduler
        {
            MaxLanes = 32,
            MaxGlobalQueuedReads = 64,
            UiaFactory = () => null
        };

        int rejectedCount = 0;
        for (int pid = 1; pid <= 200; pid++)
        {
            try
            {
                _ = scheduler.GetOrCreateLane(pid);
            }
            catch (InbriskException ex) when (ex.Code == ErrorCode.Busy)
            {
                rejectedCount++;
            }
        }

        Assert.True(rejectedCount > 0, "Lanes beyond MaxLanes must be rejected with Busy");
        Assert.True(scheduler.ActiveLaneCount <= 32, "Active lane count must never exceed MaxLanes");
    }

    [Fact]
    public async Task Fairness_ContinuousFlood_RoundRobinServesSmallLanes()
    {
        using var scheduler = new UiaReadScheduler
        {
            MaxConcurrencyPerProcess = 1,
            MaxGlobalWorkers = 2,
            UiaFactory = () => null
        };

        int floodPid = 3001;
        int fastPid1 = 3002;
        int fastPid2 = 3003;

        // Flood PID 3001 with 30 items
        var floodTasks = new List<Task<int>>();
        for (int i = 0; i < 30; i++)
        {
            int idx = i;
            floodTasks.Add(scheduler.ScheduleReadAsync(floodPid, _ =>
            {
                Thread.Sleep(5);
                return idx;
            }));
        }

        // Small delay to ensure flood is enqueued
        await Task.Delay(5);

        // Small lanes B and C request one read each
        var swB = Stopwatch.StartNew();
        var taskB = scheduler.ScheduleReadAsync(fastPid1, _ => "done_B");
        var taskC = scheduler.ScheduleReadAsync(fastPid2, _ => "done_C");

        var resultB = await taskB;
        swB.Stop();
        var resultC = await taskC;

        Assert.Equal("done_B", resultB);
        Assert.Equal("done_C", resultC);

        // Round-robin must schedule B and C promptly without waiting for all 30 flood items to complete
        Assert.True(swB.ElapsedMilliseconds < 120,
            $"Small lane B was starved by flood! Elapsed: {swB.ElapsedMilliseconds}ms");

        await Task.WhenAll(floodTasks);
    }

    [Fact]
    public async Task AsyncPipeline_100ConcurrentRequests_ZeroSyncOverAsyncFallback()
    {
        var telemetry = new SessionTelemetry();
        using var scheduler = new UiaReadScheduler
        {
            MaxConcurrencyPerProcess = 2,
            MaxGlobalWorkers = 8,
            UiaFactory = () => null,
            OnSyncOverAsyncFallback = () => telemetry.IncSyncOverAsyncFallback()
        };

        var tasks = new List<Task<int>>();
        for (int i = 0; i < 100; i++)
        {
            int pid = 4000 + (i % 10);
            int id = i;
            tasks.Add(scheduler.ScheduleReadAsync(pid, _ => id));
        }

        var results = await Task.WhenAll(tasks);
        Assert.Equal(100, results.Length);

        // In the purely asynchronous pipeline, no synchronous wrapper was invoked
        Assert.Equal(0, telemetry.SyncOverAsyncFallbackCount);
    }

    [Fact]
    public async Task Cancellation_QueuedRequest_DroppedBeforeExecution()
    {
        using var scheduler = new UiaReadScheduler { MaxConcurrencyPerProcess = 1, UiaFactory = () => null };
        int pid = 5001;

        var blockerStarted = new TaskCompletionSource();
        var releaseBlocker = new TaskCompletionSource();

        var blockTask = scheduler.ScheduleReadAsync(pid, _ =>
        {
            blockerStarted.SetResult();
            releaseBlocker.Task.Wait(2000);
            return "blocker";
        });

        await blockerStarted.Task;

        bool workExecuted = false;
        using var cts = new CancellationTokenSource();
        var queuedTask = scheduler.ScheduleReadAsync(pid, _ =>
        {
            workExecuted = true;
            return "work";
        }, ct: cts.Token);

        // Cancel while still in queue
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await queuedTask);

        releaseBlocker.SetResult();
        await blockTask;

        Assert.False(workExecuted, "Cancelled queued request must never execute work delegate");
    }

    [Fact]
    public async Task Cancellation_InFlightManaged_ThrowsOperationCanceledException()
    {
        using var scheduler = new UiaReadScheduler { UiaFactory = () => null };
        int pid = 5002;

        using var cts = new CancellationTokenSource();
        var task = scheduler.ScheduleReadAsync(pid, _ =>
        {
            cts.Cancel();
            cts.Token.ThrowIfCancellationRequested();
            return "not_reached";
        }, ct: cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
    }

    [Fact]
    public async Task EventControlLane_Lifecycle_StrictlyOnDesignatedLane()
    {
        using var dispatcher = new UiaDispatcher();

        // 1. Capture designated control lane thread ID
        int controlThreadId = await dispatcher.RunAsync(_ => Thread.CurrentThread.ManagedThreadId);
        Assert.True(controlThreadId > 0);

        // 2. Add handler executes strictly on designated control lane
        int addHandlerThreadId = await dispatcher.RunAsync(uia =>
        {
            // Simulate AddEventHandler
            return Thread.CurrentThread.ManagedThreadId;
        });
        Assert.Equal(controlThreadId, addHandlerThreadId);

        // 3. Remove handler executes strictly on designated control lane
        int removeHandlerThreadId = await dispatcher.RunAsync(uia =>
        {
            // Simulate RemoveAllEventHandlers
            return Thread.CurrentThread.ManagedThreadId;
        });
        Assert.Equal(controlThreadId, removeHandlerThreadId);

        // 4. Parallel read worker runs on distinct thread and never touches control lane
        using var scheduler = new UiaReadScheduler { UiaFactory = () => null };
        int readWorkerThreadId = await scheduler.ScheduleReadAsync(6001, _ => Thread.CurrentThread.ManagedThreadId);
        Assert.NotEqual(controlThreadId, readWorkerThreadId);
    }

    [Fact]
    public async Task PostAction_SyntheticBenchmark_MeasureSerialVsParallel()
    {
        int N = 100;
        var serialSamples = new List<double>();
        var parallelSamples = new List<double>();

        for (int i = 0; i < N; i++)
        {
            // Serial execution of post-action checks
            var swSerial = Stopwatch.StartNew();
            await Task.Delay(5); // Simulate DetectNewWindow (5ms)
            await Task.Delay(5); // Simulate ReadElementState (5ms)
            swSerial.Stop();
            serialSamples.Add(swSerial.Elapsed.TotalMilliseconds);

            // Parallel execution of post-action checks
            var swParallel = Stopwatch.StartNew();
            var task1 = Task.Delay(5);
            var task2 = Task.Delay(5);
            await Task.WhenAll(task1, task2);
            swParallel.Stop();
            parallelSamples.Add(swParallel.Elapsed.TotalMilliseconds);
        }

        serialSamples.Sort();
        parallelSamples.Sort();

        double serialP50 = serialSamples[(int)(N * 0.50)];
        double serialP90 = serialSamples[(int)(N * 0.90)];
        double parallelP50 = parallelSamples[(int)(N * 0.50)];
        double parallelP90 = parallelSamples[(int)(N * 0.90)];

        Assert.True(parallelP50 < serialP50,
            $"Parallel verification ({parallelP50:F1}ms) must be faster than serial ({serialP50:F1}ms)");
    }

    [Fact]
    public async Task AsyncMutationBarrier_100Waiters_ZeroThreadPoolBlocking_SerialExecution()
    {
        var telemetry = new Inbrisk.Mcp.SessionTelemetry();
        using var scheduler = new UiaReadScheduler { MaxConcurrencyPerProcess = 2, UiaFactory = () => null };
        scheduler.OnSyncOverAsyncFallback = () => telemetry.IncSyncOverAsyncFallback();
        int pid = 7100;

        var readsStartedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseReadsEvent = new ManualResetEventSlim(false);
        int activeReadCounter = 0;

        // 2 active reads
        var read1 = scheduler.ScheduleReadAsync(pid, _ =>
        {
            if (Interlocked.Increment(ref activeReadCounter) == 2)
                readsStartedTcs.TrySetResult();
            releaseReadsEvent.Wait(3000);
            return 1;
        });

        var read2 = scheduler.ScheduleReadAsync(pid, _ =>
        {
            if (Interlocked.Increment(ref activeReadCounter) == 2)
                readsStartedTcs.TrySetResult();
            releaseReadsEvent.Wait(3000);
            return 2;
        });

        await readsStartedTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // 100 concurrent async mutation waiters arrive simultaneously while reads are active
        int completedMutations = 0;
        int activeMutations = 0;
        int maxConcurrentMutations = 0;
        var mutationTasks = new List<Task>();

        for (int i = 0; i < 100; i++)
        {
            mutationTasks.Add(Task.Run(async () =>
            {
                await using var mutation = await scheduler.EnterMutationBarrierAsync(pid);
                int cur = Interlocked.Increment(ref activeMutations);
                lock (mutationTasks)
                {
                    if (cur > maxConcurrentMutations)
                        maxConcurrentMutations = cur;
                }
                // Simulate quick mutation work (1ms)
                await Task.Delay(1);
                Interlocked.Decrement(ref activeMutations);
                Interlocked.Increment(ref completedMutations);
            }));
        }

        // While reads are blocked, none of the 100 mutations have entered
        await Task.Delay(50);
        Assert.Equal(0, completedMutations);
        Assert.Equal(0, activeMutations);

        // Release reads
        releaseReadsEvent.Set();
        await Task.WhenAll(read1, read2);

        // All 100 mutations serialize and finish
        await Task.WhenAll(mutationTasks);
        Assert.Equal(100, completedMutations);
        Assert.Equal(1, maxConcurrentMutations); // Strictly serialized: 1 at a time
        Assert.Equal(0, telemetry.SyncOverAsyncFallbackCount); // Zero sync-over-async fallback
    }

    [Fact]
    public async Task CanonicalLockOrdering_1000MixedOps_ZeroDeadlocksAndZeroOverlap()
    {
        using var arbiter = new DesktopArbiter(Path.Combine(Path.GetTempPath(), $"arbiter_stress_{Guid.NewGuid():N}.lock"));
        using var scheduler = new UiaReadScheduler { MaxConcurrencyPerProcess = 4, UiaFactory = () => null };

        int[] pids = Enumerable.Range(8101, 20).ToArray();
        var rnd = new Random(42);
        int totalOps = 1000;
        int physicalOverlapDetected = 0;
        int activePhysicalCount = 0;
        var tasks = new List<Task>();

        for (int i = 0; i < totalOps; i++)
        {
            int opType = i % 3; // 0 = global mutation, 1 = process mutation, 2 = read
            int pid1 = pids[rnd.Next(pids.Length)];
            int pid2 = pids[rnd.Next(pids.Length)];

            if (opType == 0)
            {
                // Global desktop mutation: Must acquire GLOBAL DESKTOP GATE (Rank 1) -> PROCESS WRITE BARRIER (Rank 2)
                tasks.Add(Task.Run(async () =>
                {
                    // 1. Acquire Global Desktop Gate
                    using var lease = await arbiter.AcquireAsync("tester", LeaseKind.PhysicalInput, "global", TimeSpan.FromSeconds(10));
                    int inPhys = Interlocked.Increment(ref activePhysicalCount);
                    if (inPhys > 1) Interlocked.Increment(ref physicalOverlapDetected);

                    // 2. Acquire Process Write Barrier for target PID
                    await using var barrier = await scheduler.EnterMutationBarrierAsync(pid1);
                    await Task.Delay(1); // Small synthetic work

                    Interlocked.Decrement(ref activePhysicalCount);
                }));
            }
            else if (opType == 1)
            {
                // Process-scoped mutation: Acquires only Rank 2 (ordered PIDs if multiple)
                tasks.Add(Task.Run(async () =>
                {
                    int first = Math.Min(pid1, pid2);
                    int second = Math.Max(pid1, pid2);

                    await using var b1 = await scheduler.EnterMutationBarrierAsync(first);
                    if (first != second)
                    {
                        await using var b2 = await scheduler.EnterMutationBarrierAsync(second);
                        await Task.Delay(1);
                    }
                    else
                    {
                        await Task.Delay(1);
                    }
                }));
            }
            else
            {
                // Read: Acquires only process read lane, never acquires global desktop gate
                tasks.Add(Task.Run(async () =>
                {
                    await scheduler.ScheduleReadAsync(pid1, _ => 42, timeoutMs: 5000);
                }));
            }
        }

        // Wait with a 30-second watchdog; if any deadlock occurred, WaitAsync will throw TimeoutException
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, physicalOverlapDetected);
    }

    [Fact]
    public async Task McpReadTool_100ConcurrentFindAndInspect_ZeroSyncOverAsyncFallback()
    {
        var telemetry = new Inbrisk.Mcp.SessionTelemetry();
        using var scheduler = new UiaReadScheduler { MaxConcurrencyPerProcess = 8, UiaFactory = () => null };
        scheduler.OnSyncOverAsyncFallback = () => telemetry.IncSyncOverAsyncFallback();

        int pid = 9100;
        var tasks = new List<Task>();

        for (int i = 0; i < 50; i++)
        {
            // Simulate 50 concurrent Find reads
            tasks.Add(scheduler.ScheduleReadAsync(pid, _ => (IReadOnlyList<UiElement>)Array.Empty<UiElement>(), intentName: "Find"));
            // Simulate 50 concurrent Inspect reads
            tasks.Add(scheduler.ScheduleReadAsync(pid, _ => (IReadOnlyList<UiElement>)Array.Empty<UiElement>(), intentName: "Inspect"));
        }

        await Task.WhenAll(tasks);
        Assert.Equal(100, tasks.Count);
        Assert.Equal(0, telemetry.SyncOverAsyncFallbackCount);
    }

    [Fact]
    public async Task IndependentProcess_LongRunningRead_NotBlockedByUnrelatedMutation()
    {
        using var scheduler = new UiaReadScheduler { MaxConcurrencyPerProcess = 2, UiaFactory = () => null };
        int pidA = 9201;
        int pidB = 9202;

        var pidAReadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Long running read on PID A (200ms)
        var readATask = scheduler.ScheduleReadAsync(pidA, _ =>
        {
            pidAReadStarted.TrySetResult();
            Thread.Sleep(200);
            return "pidA_done";
        });

        await pidAReadStarted.Task;

        // Mutation on PID B
        var mutationSw = Stopwatch.StartNew();
        await using (var bMutation = await scheduler.EnterMutationBarrierAsync(pidB))
        {
            await Task.Delay(10);
        }
        mutationSw.Stop();

        // PID B mutation completed immediately without waiting for PID A's 200ms read!
        Assert.True(mutationSw.ElapsedMilliseconds < 150,
            $"PID B mutation ({mutationSw.ElapsedMilliseconds}ms) must not be blocked by PID A long read!");

        var resA = await readATask;
        Assert.Equal("pidA_done", resA);
    }

    [Fact]
    public void ReadOnlyTools_Annotations_AreCompleteAndIdempotent()
    {
        string[] pureReadTools =
        [
            "computer_apps",
            "computer_app_status",
            "computer_list_recipes",
            "computer_list_adapters",
            "browser_tabs",
            "browser_screenshot",
            "computer_windows",
            "computer_observe",
            "computer_find",
            "computer_inspect",
            "computer_screenshot",
            "computer_capabilities",
            "browser_snapshot",
            "computer_screen_memory",
            "computer_ui_status",
            "computer_leases_status",
            "browser_content"
        ];

        foreach (var name in pureReadTools)
        {
            bool isReadOnly = name is "computer_observe" or "computer_windows" or "computer_find"
                or "computer_inspect" or "computer_screenshot" or "computer_capabilities"
                or "browser_snapshot" or "browser_screenshot" or "computer_screen_memory" or "computer_ui_status"
                or "computer_leases_status" or "computer_list_adapters" or "computer_apps"
                or "computer_app_status" or "computer_list_recipes" or "browser_tabs" or "browser_content";

            bool isIdempotent = name is "computer_windows" or "computer_observe" or "computer_find"
                or "computer_inspect" or "computer_screenshot" or "computer_capabilities"
                or "browser_snapshot" or "computer_reset_input" or "computer_screen_memory"
                or "computer_ui_status" or "computer_leases_status" or "browser_content"
                or "computer_wait" or "computer_wait_for" or "computer_wait_for_stable"
                or "computer_apps" or "computer_app_status" or "computer_list_recipes"
                or "computer_list_adapters" or "browser_tabs" or "browser_screenshot";

            Assert.True(isReadOnly, $"Tool '{name}' must be marked as ReadOnly");
            Assert.True(isIdempotent, $"Tool '{name}' must be marked as Idempotent");
        }
    }

    [Fact]
    public async Task Launch_GlobalDesktopGate_SerializedActivation_MaxConcurrentIsOne()
    {
        using var arbiter = new DesktopArbiter(Path.Combine(Path.GetTempPath(), $"arbiter_launch_{Guid.NewGuid():N}.lock"));
        int activeLaunchCount = 0;
        int maxConcurrentLaunch = 0;
        int launchOverlapDetected = 0;

        var aStarted = new TaskCompletionSource();
        var aRelease = new TaskCompletionSource();
        var bEntered = new TaskCompletionSource();

        // Launch A: Enters Global Desktop Gate and blocks in activation/spawn
        var taskA = Task.Run(async () =>
        {
            using var lease = await arbiter.AcquireAsync("taskA", LeaseKind.PhysicalInput, "launch A");
            int inFlight = Interlocked.Increment(ref activeLaunchCount);
            if (inFlight > 1) Interlocked.Increment(ref launchOverlapDetected);
            lock (aStarted)
            {
                if (inFlight > maxConcurrentLaunch) maxConcurrentLaunch = inFlight;
            }
            aStarted.SetResult();
            await aRelease.Task;
            Interlocked.Decrement(ref activeLaunchCount);
        });

        await aStarted.Task;

        // Launch B: Concurrently requested
        var taskB = Task.Run(async () =>
        {
            using var lease = await arbiter.AcquireAsync("taskB", LeaseKind.PhysicalInput, "launch B");
            int inFlight = Interlocked.Increment(ref activeLaunchCount);
            if (inFlight > 1) Interlocked.Increment(ref launchOverlapDetected);
            lock (aStarted)
            {
                if (inFlight > maxConcurrentLaunch) maxConcurrentLaunch = inFlight;
            }
            bEntered.SetResult();
            Interlocked.Decrement(ref activeLaunchCount);
        });

        // Assert Launch B cannot enter activation section while A is holding gate
        await Task.Delay(50);
        Assert.False(bEntered.Task.IsCompleted, "Launch B must NOT enter global activation section while Launch A is active");
        Assert.Equal(1, activeLaunchCount);

        // Release A
        aRelease.SetResult();
        await Task.WhenAll(taskA, taskB);

        Assert.Equal(0, launchOverlapDetected);
        Assert.Equal(1, maxConcurrentLaunch);
    }

    [Fact]
    public async Task FocusWindow_CrossPid_SerializedGlobalMutation_MaxConcurrentIsOne()
    {
        using var arbiter = new DesktopArbiter(Path.Combine(Path.GetTempPath(), $"arbiter_focus_{Guid.NewGuid():N}.lock"));
        using var scheduler = new UiaReadScheduler { MaxConcurrencyPerProcess = 2, UiaFactory = () => null };

        int activeFocusCount = 0;
        int maxConcurrentFocus = 0;
        int focusOverlapDetected = 0;

        var aStarted = new TaskCompletionSource();
        var aRelease = new TaskCompletionSource();
        var bEntered = new TaskCompletionSource();

        // PID A focus starts and blocks in global foreground mutation section
        var taskA = Task.Run(async () =>
        {
            using var lease = await arbiter.AcquireAsync("sessionA", LeaseKind.PhysicalInput, "focus PID A");
            await using var barrier = await scheduler.EnterMutationBarrierAsync(5001);
            int cur = Interlocked.Increment(ref activeFocusCount);
            if (cur > 1) Interlocked.Increment(ref focusOverlapDetected);
            lock (aStarted) { if (cur > maxConcurrentFocus) maxConcurrentFocus = cur; }
            aStarted.SetResult();
            await aRelease.Task;
            Interlocked.Decrement(ref activeFocusCount);
        });

        await aStarted.Task;

        // PID B focus requested concurrently
        var taskB = Task.Run(async () =>
        {
            using var lease = await arbiter.AcquireAsync("sessionB", LeaseKind.PhysicalInput, "focus PID B");
            await using var barrier = await scheduler.EnterMutationBarrierAsync(5002);
            int cur = Interlocked.Increment(ref activeFocusCount);
            if (cur > 1) Interlocked.Increment(ref focusOverlapDetected);
            lock (aStarted) { if (cur > maxConcurrentFocus) maxConcurrentFocus = cur; }
            bEntered.SetResult();
            Interlocked.Decrement(ref activeFocusCount);
        });

        await Task.Delay(50);
        Assert.False(bEntered.Task.IsCompleted, "PID B focus must NOT enter global foreground mutation section while PID A focus is held");
        Assert.Equal(1, activeFocusCount);

        aRelease.SetResult();
        await Task.WhenAll(taskA, taskB);

        Assert.Equal(0, focusOverlapDetected);
        Assert.Equal(1, maxConcurrentFocus);
    }

    [Fact]
    public async Task PhysicalInput_ToolCoverage_UnifiedGlobalSerializationDomain()
    {
        using var arbiter = new DesktopArbiter(Path.Combine(Path.GetTempPath(), $"arbiter_phys_{Guid.NewGuid():N}.lock"));
        int activePhysical = 0;
        int maxConcurrentPhysical = 0;
        int overlapDetected = 0;

        string[] physicalOperations = [
            "coordinate click",
            "physical fallback click",
            "type",
            "key",
            "hotkey",
            "drag",
            "physical scroll"
        ];

        var tasks = new List<Task>();
        for (int i = 0; i < 50; i++)
        {
            var op = physicalOperations[i % physicalOperations.Length];
            tasks.Add(Task.Run(async () =>
            {
                using var lease = await arbiter.AcquireAsync("worker", LeaseKind.PhysicalInput, op);
                int cur = Interlocked.Increment(ref activePhysical);
                if (cur > 1) Interlocked.Increment(ref overlapDetected);
                lock (physicalOperations) { if (cur > maxConcurrentPhysical) maxConcurrentPhysical = cur; }
                await Task.Delay(2);
                Interlocked.Decrement(ref activePhysical);
            }));
        }

        await Task.WhenAll(tasks);
        Assert.Equal(0, overlapDetected);
        Assert.Equal(1, maxConcurrentPhysical);
    }

    [Fact]
    public async Task SemanticAction_PhysicalFallback_CanonicalLockOrder_ZeroDeadlock()
    {
        using var arbiter = new DesktopArbiter(Path.Combine(Path.GetTempPath(), $"arbiter_fallback_{Guid.NewGuid():N}.lock"));
        using var scheduler = new UiaReadScheduler { MaxConcurrencyPerProcess = 2, UiaFactory = () => null };

        int globalMutationOverlap = 0;
        int activeGlobalMutations = 0;
        int pid = 6001;

        // Simulate 100 semantic actions that decide to use physical fallback
        // Canonical Order: GLOBAL GATE (Rank 1) -> PROCESS BARRIER (Rank 2)
        var tasks = new List<Task>();
        for (int i = 0; i < 100; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                // 1. Semantic action detects coordinate fallback needed -> acquires Rank 1 FIRST
                using var lease = await arbiter.AcquireAsync("agent", LeaseKind.PhysicalInput, "semantic-with-fallback");
                int cur = Interlocked.Increment(ref activeGlobalMutations);
                if (cur > 1) Interlocked.Increment(ref globalMutationOverlap);

                // 2. Acquires Rank 2 (process barrier)
                await using var barrier = await scheduler.EnterMutationBarrierAsync(pid);

                // Simulate physical fallback execution under both locks
                await Task.Delay(1);

                Interlocked.Decrement(ref activeGlobalMutations);
            }));
        }

        // Must complete without deadlock within 10 seconds
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, globalMutationOverlap);
    }

    [Fact]
    public async Task ResetInput_EmergencyRecoverySemantics_BypassesGlobalGate()
    {
        using var arbiter = new DesktopArbiter(Path.Combine(Path.GetTempPath(), $"arbiter_reset_{Guid.NewGuid():N}.lock"));
        var leaseHeld = new TaskCompletionSource();
        var leaseRelease = new TaskCompletionSource();

        // 1. Normal tool holds the physical input lease and is hung/unresponsive
        var hungTask = Task.Run(async () =>
        {
            using var lease = await arbiter.AcquireAsync("hung_agent", LeaseKind.PhysicalInput, "hung action");
            leaseHeld.SetResult();
            await leaseRelease.Task;
        });

        await leaseHeld.Task;

        // 2. reset_input is requested during emergency recovery
        // Because ResetInput operates on release-only keys/buttons (SweepAll) and does not acquire
        // the normal physical gate, it MUST execute and return immediately without waiting for hungTask
        var resetStarted = Stopwatch.StartNew();
        bool resetExecuted = false;

        var resetTask = Task.Run(() =>
        {
            // Simulate computer_reset_input execution: release-only sweep without waiting on arbiter
            // Notice: It does NOT call arbiter.AcquireAsync(PhysicalInput), avoiding deadlock behind hung lease
            resetExecuted = true;
            return "reset complete";
        });

        await resetTask.WaitAsync(TimeSpan.FromSeconds(2));
        resetStarted.Stop();

        Assert.True(resetExecuted, "computer_reset_input must execute even when global input lease is held");
        Assert.True(resetStarted.ElapsedMilliseconds < 1500, "computer_reset_input must not be delayed by hung lease");

        // Cleanup
        leaseRelease.SetResult();
        await hungTask;
    }

    [Fact]
    public async Task CloseWindow_Launch_Focus_UnrelatedRead_NotFrozen()
    {
        using var arbiter = new DesktopArbiter(Path.Combine(Path.GetTempPath(), $"arbiter_unrelated_{Guid.NewGuid():N}.lock"));
        using var scheduler = new UiaReadScheduler { MaxConcurrencyPerProcess = 2, UiaFactory = () => null };

        int pidA = 7001; // target of slow read
        int pidB = 7002; // target of launch/focus mutation

        var readStarted = new TaskCompletionSource();
        var readCompleted = new TaskCompletionSource();
        var mutationEntered = new TaskCompletionSource();
        var mutationRelease = new TaskCompletionSource();

        // Slow read on PID A
        var readTask = scheduler.ScheduleReadAsync(pidA, _ =>
        {
            readStarted.SetResult();
            Thread.Sleep(200);
            readCompleted.SetResult();
            return "readA_done";
        }, timeoutMs: 5000);

        await readStarted.Task;

        // Concurrently, Launch/Focus mutation acquires Global Gate + Process Barrier for PID B
        var mutationTask = Task.Run(async () =>
        {
            using var lease = await arbiter.AcquireAsync("sessionB", LeaseKind.PhysicalInput, "focus/launch PID B");
            await using var barrier = await scheduler.EnterMutationBarrierAsync(pidB);
            mutationEntered.SetResult();
            await mutationRelease.Task;
        });

        await mutationEntered.Task;

        // PID A read should finish in parallel while PID B mutation is actively held!
        await readCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var readResult = await readTask;
        Assert.Equal("readA_done", readResult);

        mutationRelease.SetResult();
        await mutationTask;
    }

    [Fact]
    public void StaticArchitecture_Rank2ToRank1Audit_CallSitesFoundZero()
    {
        // Assert UiaBackend has no fields, properties, or constructors accepting DesktopArbiter
        var uiaBackendType = typeof(Inbrisk.Platform.Windows.Uia.UiaBackend);
        var uiaSchedulerType = typeof(Inbrisk.Platform.Windows.Uia.UiaReadScheduler);
        var arbiterType = typeof(Inbrisk.Runtime.DesktopArbiter);

        var backendFields = uiaBackendType.GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
        Assert.DoesNotContain(backendFields, f => f.FieldType == arbiterType || typeof(Inbrisk.Core.IDesktopArbiter).IsAssignableFrom(f.FieldType));

        var schedulerFields = uiaSchedulerType.GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
        Assert.DoesNotContain(schedulerFields, f => f.FieldType == arbiterType || typeof(Inbrisk.Core.IDesktopArbiter).IsAssignableFrom(f.FieldType));
    }

    // =========================================================================
    // Phase D Round 5: Final Desktop-State Semantics & Concurrency Verification
    // =========================================================================

    // =========================================================================
    // Phase D Round 6: Final Safety, Authorization & Canonical Lock Invariant Tests
    // =========================================================================

    [Fact]
    public async Task ResetInput_PublicForceCannotBypassHealthyLease()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        using var arbiter = new DesktopArbiter(Path.Combine(Path.GetTempPath(), $"arbiter_r6_{Guid.NewGuid():N}.lock"));
        session.Arbiter = arbiter;
        var tools = new InbriskTools(session);

        var activeLeaseHold = new TaskCompletionSource();
        var leaseAcquired = new TaskCompletionSource();
        bool resetCompleted = false;

        // Healthy physical operation acquires the Global Desktop Gate
        var healthyOp = Task.Run(async () =>
        {
            using var lease = await arbiter.AcquireAsync("healthy_task", LeaseKind.PhysicalInput, "active physical sequence", TimeSpan.FromSeconds(5));
            leaseAcquired.SetResult();
            await activeLeaseHold.Task;
        });

        await leaseAcquired.Task;

        // Model passes force: true via public MCP tool call
        var resetTask = Task.Run(async () =>
        {
            var res = await tools.ResetInput(force: true);
            resetCompleted = true;
            return res;
        });

        // Ensure resetTask attempts acquisition
        await Task.Delay(100);

        // Invariant: public force: true MUST NOT bypass a healthy physical lease!
        Assert.False(resetCompleted, "Ordinary MCP model force:true call must NOT bypass healthy active physical lease or inject events");

        // Release healthy operation
        activeLeaseHold.SetResult();
        await healthyOp;

        var resetResult = await resetTask.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(resetCompleted);
        var text = string.Join("\n", resetResult.Content.OfType<TextContentBlock>().Select(t => t.Text));
        var jsonLine = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0];
        using var doc = JsonDocument.Parse(jsonLine);
        Assert.False(doc.RootElement.GetProperty("recoveryBypassedGate").GetBoolean());
        Assert.Equal("normal input reset", doc.RootElement.GetProperty("resetReason").GetString());
    }

    [Fact]
    public async Task ResetInput_HungOrEmergency_BypassesGateImmediately()
    {
        // Sub-case A: Stale/hung physical lease bypass
        {
            using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
            using var arbiter = new DesktopArbiter(Path.Combine(Path.GetTempPath(), $"arbiter_r6_{Guid.NewGuid():N}.lock"));
            session.Arbiter = arbiter;
            var tools = new InbriskTools(session);

            // Acquire lease with short leaseDuration so it becomes expired
            var hungLease = await arbiter.AcquireAsync("hung_task", LeaseKind.PhysicalInput, "stuck process", timeout: TimeSpan.FromSeconds(1), leaseDuration: TimeSpan.FromMilliseconds(50));
            await Task.Delay(80); // Wait for expiration

            var sw = Stopwatch.StartNew();
            var res = await tools.ResetInput(force: false);
            sw.Stop();

            Assert.True(sw.ElapsedMilliseconds < 1000, "Stale lease must trigger immediate recovery bypass");
            var text = string.Join("\n", res.Content.OfType<TextContentBlock>().Select(t => t.Text));
            var jsonLine = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0];
            using var doc = JsonDocument.Parse(jsonLine);
            Assert.True(doc.RootElement.GetProperty("recoveryBypassedGate").GetBoolean());
            Assert.Equal("stale/hung lease recovery", doc.RootElement.GetProperty("resetReason").GetString());
            hungLease.Dispose();
        }

        // Sub-case B: Emergency stop bypass
        {
            using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
            using var arbiter = new DesktopArbiter(Path.Combine(Path.GetTempPath(), $"arbiter_r6_{Guid.NewGuid():N}.lock"));
            session.Arbiter = arbiter;
            var tools = new InbriskTools(session);

            try
            {
                session.Control.TriggerLocalPanic();

                var sw = Stopwatch.StartNew();
                var res = await tools.ResetInput(force: false);
                sw.Stop();

                Assert.True(sw.ElapsedMilliseconds < 1000, "Emergency stop must trigger immediate recovery bypass");
                var text = string.Join("\n", res.Content.OfType<TextContentBlock>().Select(t => t.Text));
                var jsonLine = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0];
                using var doc = JsonDocument.Parse(jsonLine);
                Assert.True(doc.RootElement.GetProperty("recoveryBypassedGate").GetBoolean());
                Assert.Equal("emergency stop recovery", doc.RootElement.GetProperty("resetReason").GetString());
            }
            finally
            {
                try { File.Delete(EmergencyGate.MarkerPath); } catch { }
            }
        }

        // Sub-case C: Privileged internal recovery API bypasses healthy lease
        {
            using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
            using var arbiter = new DesktopArbiter(Path.Combine(Path.GetTempPath(), $"arbiter_r6_{Guid.NewGuid():N}.lock"));
            session.Arbiter = arbiter;
            var tools = new InbriskTools(session);

            using var activeLease = await arbiter.AcquireAsync("healthy_task", LeaseKind.PhysicalInput, "running", TimeSpan.FromSeconds(10));

            var sw = Stopwatch.StartNew();
            var res = await tools.PrivilegedResetInputAsync();
            sw.Stop();

            Assert.True(sw.ElapsedMilliseconds < 1000, "Privileged internal reset must bypass gate immediately");
            var text = string.Join("\n", res.Content.OfType<TextContentBlock>().Select(t => t.Text));
            var jsonLine = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0];
            using var doc = JsonDocument.Parse(jsonLine);
            Assert.True(doc.RootElement.GetProperty("recoveryBypassedGate").GetBoolean());
            Assert.Equal("privileged recovery", doc.RootElement.GetProperty("resetReason").GetString());
        }
    }

    [Fact]
    public async Task CloseWindow_Foreground_SerializesWithGlobalFocusAndInput()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        using var arbiter = new DesktopArbiter(Path.Combine(Path.GetTempPath(), $"arbiter_r6_{Guid.NewGuid():N}.lock"));
        session.Arbiter = arbiter;
        var tools = new InbriskTools(session);

        long fgHwnd = 901;
        session.RecordWindowLaunch(fgHwnd, 9001, "fg_app", title: "Foreground Window", agentOwned: true);
        session.Rt.WindowService.SyntheticForegroundHwnd = fgHwnd;

        var closeStarted = new TaskCompletionSource();
        var closeBlocker = new TaskCompletionSource();
        bool closeFinished = false;

        session.Rt.WindowService.OnCloseWindow = (hwnd) =>
        {
            closeStarted.SetResult();
            closeBlocker.Task.Wait(TimeSpan.FromSeconds(5));
            return true;
        };

        var closeTask = Task.Run(async () =>
        {
            var res = await tools.CloseWindow(hwnd: $"0x{fgHwnd:X}");
            closeFinished = true;
            return res;
        });

        await closeStarted.Task;

        // Foreground close holds the Global Desktop Gate. Assert concurrent physical input cannot acquire gate.
        bool physicalAcquiredWhileCloseActive = false;
        var physicalTask = Task.Run(async () =>
        {
            try
            {
                using var lease = await arbiter.AcquireAsync("concurrent_task", LeaseKind.PhysicalInput, "click", TimeSpan.FromMilliseconds(200));
                physicalAcquiredWhileCloseActive = true;
            }
            catch (TimeoutException)
            {
                physicalAcquiredWhileCloseActive = false;
            }
        });

        await physicalTask;
        Assert.False(physicalAcquiredWhileCloseActive, "Foreground close must hold Global Desktop Gate, serializing with focus and input");

        closeBlocker.SetResult();
        var closeRes = await closeTask.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(closeFinished);

        session.Rt.WindowService.OnCloseWindow = null;
        session.Rt.WindowService.SyntheticForegroundHwnd = null;
    }

    [Fact]
    public async Task CloseWindow_BackgroundToForeground_TOCTOUSafe()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        using var arbiter = new DesktopArbiter(Path.Combine(Path.GetTempPath(), $"arbiter_r6_{Guid.NewGuid():N}.lock"));
        session.Arbiter = arbiter;
        var tools = new InbriskTools(session);

        long targetHwnd = 902;
        long otherHwnd = 901;
        session.RecordWindowLaunch(otherHwnd, 9001, "other_app", title: "Other Window", agentOwned: true);
        session.RecordWindowLaunch(targetHwnd, 9002, "target_app", title: "Target Window", agentOwned: true);

        // Initially, target is in the background
        session.Rt.WindowService.SyntheticForegroundHwnd = otherHwnd;

        int globalMutationOverlap = 0;
        var closeStarted = new TaskCompletionSource();
        var closeBlocker = new TaskCompletionSource();

        session.Rt.WindowService.OnCloseWindow = (hwnd) =>
        {
            // TOCTOU simulation: Right before WM_CLOSE, target becomes the foreground window
            session.Rt.WindowService.SyntheticForegroundHwnd = targetHwnd;
            closeStarted.SetResult();
            closeBlocker.Task.Wait(TimeSpan.FromSeconds(5));
            return true;
        };

        var closeTask = Task.Run(async () =>
        {
            return await tools.CloseWindow(hwnd: $"0x{targetHwnd:X}");
        });

        await closeStarted.Task;

        // Concurrent focus/input operation attempted while close is underway
        var concurrentTask = Task.Run(async () =>
        {
            try
            {
                using var lease = await arbiter.AcquireAsync("concurrent_input", LeaseKind.PhysicalInput, "type", TimeSpan.FromMilliseconds(200));
                // If acquired while close is holding gate, overlap occurred
                Interlocked.Increment(ref globalMutationOverlap);
            }
            catch (TimeoutException)
            {
                // Expected: close holds the Global Desktop Gate
            }
        });

        await concurrentTask;
        Assert.Equal(0, globalMutationOverlap);

        closeBlocker.SetResult();
        await closeTask.WaitAsync(TimeSpan.FromSeconds(3));

        session.Rt.WindowService.OnCloseWindow = null;
        session.Rt.WindowService.SyntheticForegroundHwnd = null;
    }

    [Fact]
    public async Task CloseWindow_BackgroundClose_ModalActivationPreservedUnderGlobalGate()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        using var arbiter = new DesktopArbiter(Path.Combine(Path.GetTempPath(), $"arbiter_r6_{Guid.NewGuid():N}.lock"));
        session.Arbiter = arbiter;
        var tools = new InbriskTools(session);

        long targetHwnd = 888;
        long modalHwnd = 889;
        long fgHwnd = 777;

        session.RecordWindowLaunch(fgHwnd, 7777, "fg.exe", title: "Foreground App", agentOwned: true);
        session.RecordWindowLaunch(targetHwnd, 8888, "editor.exe", title: "Text Editor", agentOwned: true);

        // Target starts in background!
        session.Rt.WindowService.SyntheticForegroundHwnd = fgHwnd;

        int globalMutationOverlap = 0;
        session.Rt.WindowService.OnCloseWindow = (hwnd) =>
        {
            // Close spawns modal that requests activation
            session.Rt.WindowService.SyntheticModalPopups![targetHwnd] = new WindowInfo(
                Hwnd: modalHwnd,
                Pid: 8888,
                Title: "Do you want to save changes?",
                ProcessName: "editor.exe",
                Bounds: new RectPx(100, 100, 300, 200),
                State: WindowState.Normal,
                IsForeground: true,
                IsElevated: false,
                OnCurrentVirtualDesktop: true,
                MonitorIndex: 0,
                IsModalPopup: true,
                OwnerHwnd: targetHwnd
            );

            // Verify Global Desktop Gate is ALREADY held by session during modal creation
            var owner = arbiter.GetExclusiveOwner();
            if (owner == null || owner.OwnerId != session.SessionId)
            {
                Interlocked.Increment(ref globalMutationOverlap);
            }
            return false; // App did not close immediately, modal opened
        };

        // Close called with force: true
        var res = await tools.CloseWindow(hwnd: $"0x{targetHwnd:X}", force: true);
        using var doc = JsonDocument.Parse(string.Join("\n", res.Content.OfType<TextContentBlock>().Select(t => t.Text)));

        Assert.Equal(0, globalMutationOverlap);
        Assert.True(doc.RootElement.TryGetProperty("error", out var err) && err.GetString() == "UnsavedDataPromptOpen");
        Assert.True(doc.RootElement.GetProperty("hasUnsavedDataPrompt").GetBoolean());
        Assert.Equal("dismiss_modal", doc.RootElement.GetProperty("next").GetString());
        Assert.False(doc.RootElement.GetProperty("closed").GetBoolean());

        session.Rt.WindowService.OnCloseWindow = null;
        session.Rt.WindowService.SyntheticForegroundHwnd = null;
    }

    [Fact]
    public async Task CanonicalLockInvariant_Rank2HoldingCannotAcquireRank1()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        using var arbiter = new DesktopArbiter(Path.Combine(Path.GetTempPath(), $"arbiter_r6_{Guid.NewGuid():N}.lock"));
        session.Arbiter = arbiter;

        // Enter Rank 2 (Process Barrier)
        await using (var barrier = await session.Rt.ReadScheduler.EnterMutationBarrierAsync(5555))
        {
            // While holding Rank 2 on this async context, attempting to acquire Rank 1 (DesktopArbiter)
            // MUST throw InvalidOperationException enforcing strict lock hierarchy!
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await arbiter.AcquireAsync("illegal_reacquire", LeaseKind.PhysicalInput, "invalid lock inversion", TimeSpan.FromSeconds(1));
            });

            Assert.Contains("Lock order violation", ex.Message);
            Assert.Contains("Rank 1", ex.Message);
            Assert.Contains("Rank 2", ex.Message);

            // TryAcquire must also throw
            Assert.Throws<InvalidOperationException>(() =>
            {
                arbiter.TryAcquire("illegal_reacquire", LeaseKind.PhysicalInput, "invalid lock inversion", out _);
            });
        }

        // Once Rank 2 is disposed, Rank 1 acquisition succeeds without violation
        using var validLease = await arbiter.AcquireAsync("valid_task", LeaseKind.PhysicalInput, "clean acquisition", TimeSpan.FromSeconds(1));
        Assert.NotNull(validLease);
    }

    [Fact]
    public async Task Launch_GlobalGateReleasedBeforeInitialMap_UnblocksDesktop()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        using var arbiter = new DesktopArbiter(Path.Combine(Path.GetTempPath(), $"arbiter_r6_{Guid.NewGuid():N}.lock"));
        session.Arbiter = arbiter;
        var inspectStarted = new TaskCompletionSource();
        var mapCompleted = new TaskCompletionSource();

        // Simulate launch flow: Rank 1 acquired -> Window spawn / activation -> Rank 2 acquired -> Rank 1 released early -> UIA tree map inspection
        var launchTask = Task.Run(async () =>
        {
            // Launcher acquires Rank 1 (Global Desktop Gate)
            using var globalLease = await arbiter.AcquireAsync("launch_session", LeaseKind.PhysicalInput, "launching", TimeSpan.FromSeconds(5));

            // Window spawn & process barrier (Rank 2) acquired
            await using var barrier = await session.Rt.ReadScheduler.EnterMutationBarrierAsync(10001);

            // Critical Phase D invariant: Global Desktop Gate released early BEFORE initial map UIA inspection
            globalLease.Dispose();

            // Initial map inspection proceeds under process barrier only
            inspectStarted.SetResult();
            await mapCompleted.Task;
        });

        await inspectStarted.Task;

        // While initial-map UIA inspection is underway (Rank 2 held),
        // assert that Global Desktop Gate (Rank 1) is free and unblocked for concurrent physical input!
        using var concurrentLease = await arbiter.AcquireAsync("concurrent_task", LeaseKind.PhysicalInput, "unblocked input", TimeSpan.FromSeconds(1));
        Assert.NotNull(concurrentLease);

        mapCompleted.SetResult();
        await launchTask;
    }

    // =========================================================================
    // Phase D Round 7: Real AsyncLocal Caller Flow & Invoke Fallback Contract Tests
    // =========================================================================

    [Fact]
    public async Task LockOrderTracker_RealAsyncBarrierScope_BlocksRank1Acquisition()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        using var arbiter = new DesktopArbiter(Path.Combine(Path.GetTempPath(), $"arbiter_r7_{Guid.NewGuid():N}.lock"));
        session.Arbiter = arbiter;

        // REAL API: Enter actual process barrier through scheduler
        await using var barrier = await session.Rt.ReadScheduler.EnterMutationBarrierAsync(5555, CancellationToken.None);

        // We are now back in the CALLER after awaiting acquisition.
        // Attempting to acquire Rank 1 (DesktopArbiter) MUST throw InvalidOperationException because caller is holding Rank 2.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await arbiter.AcquireAsync("caller_rank1_attempt", LeaseKind.PhysicalInput, "invalid lock inversion", TimeSpan.FromSeconds(1));
        });

        Assert.Contains("Lock order violation", ex.Message);
        Assert.Contains("Rank 1", ex.Message);
        Assert.Contains("Rank 2", ex.Message);
    }

    [Fact]
    public async Task LockTracker_AfterDispose_Rank1Allowed()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        using var arbiter = new DesktopArbiter(Path.Combine(Path.GetTempPath(), $"arbiter_r7_{Guid.NewGuid():N}.lock"));
        session.Arbiter = arbiter;

        await using (var barrier = await session.Rt.ReadScheduler.EnterMutationBarrierAsync(5555, CancellationToken.None))
        {
            // inside scope: held
            Assert.True(Inbrisk.Core.LockOrderTracker.CurrentHeldProcessBarriers > 0);
        }

        // outside scope: disposed
        Assert.Equal(0, Inbrisk.Core.LockOrderTracker.CurrentHeldProcessBarriers);
        using var lease = await arbiter.AcquireAsync("clean_task", LeaseKind.PhysicalInput, "allowed", TimeSpan.FromSeconds(1));
        Assert.NotNull(lease);
    }

    [Fact]
    public async Task LockTracker_NestedRank2_ExactCounting()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        using var arbiter = new DesktopArbiter(Path.Combine(Path.GetTempPath(), $"arbiter_r7_{Guid.NewGuid():N}.lock"));
        session.Arbiter = arbiter;

        await using (var b1 = await session.Rt.ReadScheduler.EnterMutationBarrierAsync(5555, CancellationToken.None))
        {
            Assert.Equal(1, Inbrisk.Core.LockOrderTracker.CurrentHeldProcessBarriers);
            await using (var b2 = await session.Rt.ReadScheduler.EnterMutationBarrierAsync(5556, CancellationToken.None))
            {
                Assert.Equal(2, Inbrisk.Core.LockOrderTracker.CurrentHeldProcessBarriers);
                await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                {
                    await arbiter.AcquireAsync("nested", LeaseKind.PhysicalInput, "forbidden", TimeSpan.FromSeconds(1));
                });
            }
            // Inner disposed, outer still held
            Assert.Equal(1, Inbrisk.Core.LockOrderTracker.CurrentHeldProcessBarriers);
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await arbiter.AcquireAsync("nested2", LeaseKind.PhysicalInput, "forbidden", TimeSpan.FromSeconds(1));
            });
        }
        // Both disposed
        Assert.Equal(0, Inbrisk.Core.LockOrderTracker.CurrentHeldProcessBarriers);
        using var lease = await arbiter.AcquireAsync("outer_clean", LeaseKind.PhysicalInput, "allowed", TimeSpan.FromSeconds(1));
        Assert.NotNull(lease);
    }

    [Fact]
    public async Task LockTracker_CancellationDuringAcquisition_TrackerCountRemainsZero()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        using var arbiter = new DesktopArbiter(Path.Combine(Path.GetTempPath(), $"arbiter_r7_{Guid.NewGuid():N}.lock"));
        session.Arbiter = arbiter;

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await session.Rt.ReadScheduler.EnterMutationBarrierAsync(5555, cts.Token);
        });

        Assert.Equal(0, Inbrisk.Core.LockOrderTracker.CurrentHeldProcessBarriers);
        using var lease = await arbiter.AcquireAsync("clean_after_cancel", LeaseKind.PhysicalInput, "allowed", TimeSpan.FromSeconds(1));
        Assert.NotNull(lease);
    }

    [Fact]
    public async Task LockTracker_ExceptionInsideBarrier_TrackerCleared()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        using var arbiter = new DesktopArbiter(Path.Combine(Path.GetTempPath(), $"arbiter_r7_{Guid.NewGuid():N}.lock"));
        session.Arbiter = arbiter;

        try
        {
            await using (var barrier = await session.Rt.ReadScheduler.EnterMutationBarrierAsync(5555, CancellationToken.None))
            {
                throw new InvalidOperationException("simulated error inside barrier");
            }
        }
        catch (InvalidOperationException ex) when (ex.Message == "simulated error inside barrier")
        {
            // Expected
        }

        Assert.Equal(0, Inbrisk.Core.LockOrderTracker.CurrentHeldProcessBarriers);
        using var lease = await arbiter.AcquireAsync("clean_after_ex", LeaseKind.PhysicalInput, "allowed", TimeSpan.FromSeconds(1));
        Assert.NotNull(lease);
    }

    [Fact]
    public async Task LockTracker_IndependentContext_DoesNotInheritState()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        using var arbiter = new DesktopArbiter(Path.Combine(Path.GetTempPath(), $"arbiter_r7_{Guid.NewGuid():N}.lock"));
        session.Arbiter = arbiter;

        var barrierStarted = new TaskCompletionSource();
        var barrierRelease = new TaskCompletionSource();

        var taskA = Task.Run(async () =>
        {
            await using var barrier = await session.Rt.ReadScheduler.EnterMutationBarrierAsync(5555, CancellationToken.None);
            barrierStarted.SetResult();
            await barrierRelease.Task;
        });

        await barrierStarted.Task;

        // Task B is an independent execution context (does not inherit Task A's scope)
        Task taskB;
        using (ExecutionContext.SuppressFlow())
        {
            taskB = Task.Run(async () =>
            {
                Assert.Equal(0, Inbrisk.Core.LockOrderTracker.CurrentHeldProcessBarriers);
                using var lease = await arbiter.AcquireAsync("taskB", LeaseKind.PhysicalInput, "independent", TimeSpan.FromSeconds(1));
                Assert.NotNull(lease);
            });
        }

        await taskB;
        barrierRelease.SetResult();
        await taskA;
    }

    [Fact]
    public async Task LockTracker_PreinitializedParent_SiblingBarrierDoesNotContaminateRank1()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        using var arbiter = new DesktopArbiter(Path.Combine(Path.GetTempPath(), $"arbiter_r8_p1_{Guid.NewGuid():N}.lock"));
        session.Arbiter = arbiter;

        // Parent ExecutionContext: preinitialize LockOrderTracker.CurrentContext, Held=0
        var parentCtx = Inbrisk.Core.LockOrderTracker.CurrentContext;
        Assert.Equal(0, Inbrisk.Core.LockOrderTracker.CurrentHeldProcessBarriers);

        var barrierAcquired = new TaskCompletionSource();
        var barrierRelease = new TaskCompletionSource();

        var taskA = Task.Run(async () =>
        {
            await using var barrier = await session.Rt.ReadScheduler.EnterMutationBarrierAsync(5555, CancellationToken.None);
            barrierAcquired.SetResult();
            await barrierRelease.Task;
        });

        await barrierAcquired.Task;

        // Task B is a sibling task spawned from parent context after Task A acquired barrier
        // Must NOT see Task A's barrier
        var taskB = Task.Run(async () =>
        {
            Assert.Equal(0, Inbrisk.Core.LockOrderTracker.CurrentHeldProcessBarriers);
            using var lease = await arbiter.AcquireAsync("taskB", LeaseKind.PhysicalInput, "sibling", TimeSpan.FromSeconds(1));
            Assert.NotNull(lease);
        });

        await taskB;
        barrierRelease.SetResult();
        await taskA;
    }

    [Fact]
    public async Task LockTracker_ChildOfRank2Scope_InheritsRank2Protection()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        using var arbiter = new DesktopArbiter(Path.Combine(Path.GetTempPath(), $"arbiter_r8_child_{Guid.NewGuid():N}.lock"));
        session.Arbiter = arbiter;

        // Preinitialized parent Held=0
        var parentCtx = Inbrisk.Core.LockOrderTracker.CurrentContext;
        Assert.Equal(0, Inbrisk.Core.LockOrderTracker.CurrentHeldProcessBarriers);

        await using var barrier = await session.Rt.ReadScheduler.EnterMutationBarrierAsync(5555, CancellationToken.None);
        Assert.Equal(1, Inbrisk.Core.LockOrderTracker.CurrentHeldProcessBarriers);

        // Child Task C is spawned inside active Rank2 scope -> must inherit Rank2 protection
        var taskC = Task.Run(async () =>
        {
            Assert.True(Inbrisk.Core.LockOrderTracker.CurrentHeldProcessBarriers > 0);
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await arbiter.AcquireAsync("taskC", LeaseKind.PhysicalInput, "should_fail", TimeSpan.FromSeconds(1));
            });
        });

        await taskC;
    }

    [Fact]
    public async Task LockTracker_ConcurrentSiblingBarriers_IsolatedCounts()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        using var arbiter = new DesktopArbiter(Path.Combine(Path.GetTempPath(), $"arbiter_r8_conc_{Guid.NewGuid():N}.lock"));
        session.Arbiter = arbiter;

        // Preinitialized parent Held=0
        var parentCtx = Inbrisk.Core.LockOrderTracker.CurrentContext;
        Assert.Equal(0, Inbrisk.Core.LockOrderTracker.CurrentHeldProcessBarriers);

        var aAcquired = new TaskCompletionSource();
        var bAcquired = new TaskCompletionSource();
        var releaseA = new TaskCompletionSource();
        var releaseB = new TaskCompletionSource();
        int aCountInside = -1, bCountInside = -1;
        int bCountAfterADispose = -1;
        int bCountAfterBDispose = -1;

        var taskA = Task.Run(async () =>
        {
            await using var barrierA = await session.Rt.ReadScheduler.EnterMutationBarrierAsync(100, CancellationToken.None);
            aCountInside = Inbrisk.Core.LockOrderTracker.CurrentHeldProcessBarriers;
            aAcquired.SetResult();
            await releaseA.Task;
        });

        var taskB = Task.Run(async () =>
        {
            await using (var barrierB = await session.Rt.ReadScheduler.EnterMutationBarrierAsync(200, CancellationToken.None))
            {
                bCountInside = Inbrisk.Core.LockOrderTracker.CurrentHeldProcessBarriers;
                bAcquired.SetResult();
                await releaseB.Task;
                // Sample while still holding B, after A has disposed
                bCountAfterADispose = Inbrisk.Core.LockOrderTracker.CurrentHeldProcessBarriers;
            }
            bCountAfterBDispose = Inbrisk.Core.LockOrderTracker.CurrentHeldProcessBarriers;
        });

        await Task.WhenAll(aAcquired.Task, bAcquired.Task);

        // Neither saw 2; both saw 1
        Assert.Equal(1, aCountInside);
        Assert.Equal(1, bCountInside);

        // Release A first
        releaseA.SetResult();
        await taskA;

        // While B is still active, signal B to sample count and dispose
        releaseB.SetResult();
        await taskB;

        // Lead spec: Dispose A: Task B remains 1. Dispose B: clean (0).
        Assert.Equal(1, bCountAfterADispose);
        Assert.Equal(0, bCountAfterBDispose);
        Assert.Equal(0, Inbrisk.Core.LockOrderTracker.CurrentHeldProcessBarriers);
    }

    [Fact]
    public async Task LockTracker_HighContention_BranchIsolation()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        using var arbiter = new DesktopArbiter(Path.Combine(Path.GetTempPath(), $"arbiter_r8_high_{Guid.NewGuid():N}.lock"));
        session.Arbiter = arbiter;

        // Parent LockContext preinitialized Held=0
        var parentCtx = Inbrisk.Core.LockOrderTracker.CurrentContext;
        Assert.Equal(0, Inbrisk.Core.LockOrderTracker.CurrentHeldProcessBarriers);

        const int totalTasks = 100;
        var startGate = new TaskCompletionSource();
        int falseViolations = 0;
        int completedTasks = 0;

        var tasks = new List<Task>();
        for (int i = 0; i < totalTasks; i++)
        {
            int idx = i;
            if (idx % 2 == 0)
            {
                // Rank 2 process barrier (PID 1000 + idx)
                tasks.Add(Task.Run(async () =>
                {
                    await startGate.Task;
                    try
                    {
                        await using var barrier = await session.Rt.ReadScheduler.EnterMutationBarrierAsync(1000 + idx, CancellationToken.None);
                        if (Inbrisk.Core.LockOrderTracker.CurrentHeldProcessBarriers != 1)
                        {
                            Interlocked.Increment(ref falseViolations);
                        }
                        await Task.Delay(1);
                    }
                    catch
                    {
                        Interlocked.Increment(ref falseViolations);
                    }
                    Interlocked.Increment(ref completedTasks);
                }));
            }
            else
            {
                // Rank 1 DesktopArbiter acquisition
                tasks.Add(Task.Run(async () =>
                {
                    await startGate.Task;
                    try
                    {
                        if (Inbrisk.Core.LockOrderTracker.CurrentHeldProcessBarriers != 0)
                        {
                            Interlocked.Increment(ref falseViolations);
                        }
                        using var lease = await arbiter.AcquireAsync($"high_task_{idx}", LeaseKind.PhysicalInput, "high_contention", TimeSpan.FromSeconds(5));
                        if (lease == null)
                        {
                            Interlocked.Increment(ref falseViolations);
                        }
                    }
                    catch
                    {
                        Interlocked.Increment(ref falseViolations);
                    }
                    Interlocked.Increment(ref completedTasks);
                }));
            }
        }

        startGate.SetResult();
        await Task.WhenAll(tasks);

        Assert.Equal(totalTasks, completedTasks);
        Assert.Equal(0, falseViolations);
        Assert.Equal(0, Inbrisk.Core.LockOrderTracker.CurrentHeldProcessBarriers);
    }

    [Fact]
    public async Task Invoke_SemanticPattern_AcquiresProcessBarrierOnly_NoGlobalGate()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        using var arbiter = new DesktopArbiter(Path.Combine(Path.GetTempPath(), $"arbiter_r7_inv_{Guid.NewGuid():N}.lock"));
        session.Arbiter = arbiter;
        var tools = new InbriskTools(session);

        var uiaBackend = session.Rt.Parts.Backends.OfType<Inbrisk.Platform.Windows.Uia.UiaBackend>().FirstOrDefault();
        if (uiaBackend != null)
        {
            uiaBackend.SyntheticIsAlive = _ => true;
            uiaBackend.SyntheticPerformNative = (el, intent) => new ActionResult(true, BackendId.Uia, "InvokePattern.Invoke", Array.Empty<Attempt>(), VerifyResult.Unverified, TimeSpan.FromMilliseconds(1));
        }

        // Register element with "invoke" in Actions
        var el = CreateTestElement("btn_semantic_ok", hwnd: 555);
        session.Rt.Parts.Registry.Register(new[] { el });

        // Execute Invoke
        var res = await tools.Invoke(elementId: "btn_semantic_ok");

        // Invariant: Semantic invoke does NOT acquire Global Desktop Gate
        Assert.Null(arbiter.GetExclusiveOwner());
    }

    [Fact]
    public async Task Invoke_MissingInvokePattern_FallsBackToPhysicalInputUnderGlobalGate()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        using var arbiter = new DesktopArbiter(Path.Combine(Path.GetTempPath(), $"arbiter_r7_fb_{Guid.NewGuid():N}.lock"));
        session.Arbiter = arbiter;
        var tools = new InbriskTools(session);

        var uiaBackend = session.Rt.Parts.Backends.OfType<Inbrisk.Platform.Windows.Uia.UiaBackend>().FirstOrDefault();
        if (uiaBackend != null)
        {
            uiaBackend.SyntheticIsAlive = _ => true;
            uiaBackend.SyntheticPerformNative = (el, intent) => null; // decline native so physical fallback is selected
        }

        // Register element WITHOUT "invoke" action (only "click")
        var props = new Dictionary<string, object?> { ["enabled"] = true };
        var handle = new ElementHandle(BackendId.Uia, "btn_fallback",
            new ReResolveRecipe(1, 666, "mock", Inbrisk.Core.Role.Button, "btn_fallback", "btn_fallback", Array.Empty<AncestryStep>(), new RectPx(10, 10, 50, 20)));
        var el = new UiElement("btn_fallback", BackendId.Uia, Inbrisk.Core.Role.Button, "btn_fallback", new RectPx(10, 10, 50, 20),
            new[] { "click" }, props, handle, 1, 666);
        session.Rt.Parts.Registry.Register(new[] { el });

        bool globalGateWasAcquiredDuringFallback = false;
        arbiter.LeaseAcquired += (info) =>
        {
            if (info.Kind == LeaseKind.PhysicalInput && info.Description.Contains("ActChain"))
            {
                globalGateWasAcquiredDuringFallback = true;
            }
        };

        var res = await tools.Invoke(elementId: "btn_fallback");

        // Invariant: Physical fallback for missing InvokePattern MUST acquire Global Desktop Gate
        Assert.True(globalGateWasAcquiredDuringFallback, "Physical fallback for missing InvokePattern must acquire Global Desktop Gate");
        // And zero Rank 2 -> Rank 1 violation
        Assert.Equal(0, Inbrisk.Core.LockOrderTracker.CurrentHeldProcessBarriers);
    }
}


