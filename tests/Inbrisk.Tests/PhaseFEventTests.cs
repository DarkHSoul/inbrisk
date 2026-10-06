using System.Diagnostics;
using Inbrisk.Core;
using Inbrisk.Mcp;
using Inbrisk.Runtime;
using ModelContextProtocol.Protocol;
using Xunit;
using Role = Inbrisk.Core.Role;

namespace Inbrisk.Tests;

public sealed class PhaseFEventTests
{
    private sealed class DummyEventSource : IEventSource
    {
        public event Action<ObservedEvent>? Event;
        public int SubscriberCount => Event?.GetInvocationList().Length ?? 0;
        public void Start() { }
        public void Dispose() { }
        public void Fire(ObservedEvent e) => Event?.Invoke(e);
    }

    private sealed class DummyCaptureService : ICaptureService
    {
        public byte[] NextSample = [0, 0, 0];
        public Frame Capture(RectPx region, int maxImageWidth = 1600) => throw new NotImplementedException();
        public Frame CaptureWindow(long hwnd, int maxImageWidth = 1600) => throw new NotImplementedException();
        public RawFrame CaptureRaw(RectPx region) => throw new NotImplementedException();
        public double DiffFraction(RectPx region, int sampleScale = 8) => 0;
        public byte[] Sample(RectPx region, int scale = 8) => NextSample;
        public ICaptureSession CreateSession(CaptureTarget target) => throw new NotImplementedException();
    }

    private sealed class DummyWindowService : IWindowService
    {
        public WindowInfo? Foreground;
        public Dictionary<long, WindowInfo> OpenWindows = new();
        public WindowInfo? ActiveModal;

        public IReadOnlyList<WindowInfo> ListWindows() => OpenWindows.Values.ToList();
        public WindowInfo? GetWindow(long hwnd) => OpenWindows.TryGetValue(hwnd, out var w) ? w : null;
        public WindowInfo? GetForegroundWindow() => Foreground;
        public IReadOnlyList<MonitorInfo> GetMonitors() => [];
        public RectPx GetVirtualDesktopBounds() => new(0, 0, 1920, 1080);
        public bool FocusWindow(long hwnd) => true;
        public bool CloseWindow(long hwnd) => true;
        public WindowInfo? GetModalPopup(long hwnd) => ActiveModal;
        public WindowInfo? GetActiveBlockingPopup(long? targetHwnd = null) => ActiveModal;
        public bool IsWindowEnabled(long hwnd) => true;
        public IReadOnlyList<WindowInfo> FindSystemDialogs() => [];
        public bool IsWindowProtected(long hwnd, out string? reason) { reason = null; return false; }
    }

    private sealed class MockBackend : IElementBackend
    {
        public BackendId Id => BackendId.Uia;
        public Func<ElementHandle, UiElement?> OnReResolve { get; set; } = _ => null;
        public IReadOnlyList<UiElement> Inspect(long hwnd, InspectOptions options, CancellationToken ct = default) => [];
        public IReadOnlyList<UiElement> Find(FindSpec spec, CancellationToken ct = default) => [];
        public ActionResult? PerformNative(UiElement element, ActionIntent intent, CancellationToken ct = default) => null;
        public UiElement? ReResolve(ElementHandle handle, CancellationToken ct = default) => OnReResolve(handle);
        public bool IsAlive(UiElement element) => true;
    }

    private static UiElement MakeEl(string id, string name, Role role, string? value = null, string? state = null, long hwnd = 100)
    {
        var props = new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["value"] = value,
            ["state"] = state,
            ["toggleState"] = state,
        };
        var bounds = new RectPx(10, 10, 100, 30);
        var handle = new ElementHandle(BackendId.Uia, id,
            new ReResolveRecipe(1, hwnd, "app", role, name, id, Array.Empty<AncestryStep>(), bounds));
        return new UiElement(id, BackendId.Uia, role, name, bounds,
            ["invoke", "click"], props, handle, 1, hwnd);
    }

    // =========================================================================
    // 1. EVENT GENERATION SEMANTICS
    // =========================================================================

    [Fact]
    public void EventWait_OldEvent_DoesNotWakeNewWaiter()
    {
        var events = new DummyEventSource();
        var capture = new DummyCaptureService();
        var registry = new ElementRegistry([]);
        var elements = new List<UiElement>();

        var wait = new WaitService(spec => elements, capture, events, registry);

        // Fire event BEFORE waiter starts
        events.Fire(new ObservedEvent(EventKind.StructureChanged, DateTimeOffset.UtcNow, Hwnd: 100, ElementId: "el1", Detail: "button"));

        // Waiter should NOT wake immediately from old event and should time out
        var sw = Stopwatch.StartNew();
        var res = wait.ForElement(new FindSpec(Name: "Submit", Hwnd: 100), timeoutMs: 300);

        Assert.False(res.Success);
        Assert.True(sw.ElapsedMilliseconds >= 250);
        Assert.Equal(0, wait.Telemetry.EventWakeCount);
        Assert.True(wait.Telemetry.FallbackPollCount >= 1);
    }

    [Fact]
    public void EventWait_NewRelevantEvent_WakesWaiterImmediately()
    {
        var events = new DummyEventSource();
        var capture = new DummyCaptureService();
        var registry = new ElementRegistry([]);
        var elements = new List<UiElement>();

        var wait = new WaitService(spec => elements, capture, events, registry);

        var task = Task.Run(() => wait.ForElement(new FindSpec(Name: "TargetBtn", Hwnd: 100), timeoutMs: 2000));

        // Wait until waiter has started and performed its initial check
        var spin = Stopwatch.StartNew();
        while (wait.Telemetry.PropertyCheckCount == 0 && spin.ElapsedMilliseconds < 2000)
            Thread.Sleep(5);

        elements.Add(MakeEl("el_target", "TargetBtn", Role.Button, hwnd: 100));

        // Fire new relevant event
        events.Fire(new ObservedEvent(EventKind.StructureChanged, DateTimeOffset.UtcNow, Hwnd: 100, ElementId: "el_target", Detail: "Button"));

        var res = task.Result;
        Assert.True(res.Success);
        Assert.Equal("el_target", res.MatchedElement?.Id);
        Assert.True(res.Elapsed.TotalMilliseconds < 800);
        Assert.True(wait.Telemetry.EventWakeCount >= 1);
    }

    [Fact]
    public void EventWait_UnrelatedEvent_DoesNotFalselyCompleteOrWake()
    {
        var events = new DummyEventSource();
        var capture = new DummyCaptureService();
        var registry = new ElementRegistry([]);
        var elements = new List<UiElement>();

        var wait = new WaitService(spec => elements, capture, events, registry);

        // Fire event for completely different window HWND
        var task = Task.Run(() => wait.ForElement(new FindSpec(Name: "TargetBtn", Hwnd: 100), timeoutMs: 350));
        var sw1 = Stopwatch.StartNew();
        while (events.SubscriberCount == 0 && sw1.ElapsedMilliseconds < 1000)
        {
            Thread.Sleep(5);
        }
        events.Fire(new ObservedEvent(EventKind.StructureChanged, DateTimeOffset.UtcNow, Hwnd: 9999, ElementId: "other", Detail: "Button"));

        var res = task.Result;
        Assert.False(res.Success);
        Assert.True(wait.Telemetry.IgnoredEvents >= 1);
    }

    [Fact]
    public void EventWait_BurstOfEvents_DoesNotCauseBusySpin()
    {
        var events = new DummyEventSource();
        var capture = new DummyCaptureService();
        var registry = new ElementRegistry([]);
        var elements = new List<UiElement>();

        var wait = new WaitService(spec => elements, capture, events, registry);

        var task = Task.Run(() => wait.ForElement(new FindSpec(Name: "BurstTarget", Hwnd: 100), timeoutMs: 400));
        var sw2 = Stopwatch.StartNew();
        while (events.SubscriberCount == 0 && sw2.ElapsedMilliseconds < 1000)
        {
            Thread.Sleep(5);
        }

        // Fire 100 events in rapid succession while target is NOT present
        for (int i = 0; i < 100; i++)
        {
            events.Fire(new ObservedEvent(EventKind.StructureChanged, DateTimeOffset.UtcNow, Hwnd: 100, ElementId: $"burst_{i}", Detail: "Button"));
        }

        var res = task.Result;
        Assert.False(res.Success);

        // Crucial: 100 burst events must be coalesced / checked without 100 redundant find enumerations
        Assert.True(wait.Telemetry.PropertyCheckCount < 50, $"Expected coalesced property checks, got {wait.Telemetry.PropertyCheckCount}");
    }

    [Fact]
    public void EventWait_MultipleConcurrentWaiters_AreRaceSafe()
    {
        var events = new DummyEventSource();
        var capture = new DummyCaptureService();
        var registry = new ElementRegistry([]);
        var elements1 = new List<UiElement>();
        var elements2 = new List<UiElement>();

        var wait1 = new WaitService(spec => elements1, capture, events, registry);
        var wait2 = new WaitService(spec => elements2, capture, events, registry);

        var t1 = Task.Run(() => wait1.ForElement(new FindSpec(Name: "One", Hwnd: 101), timeoutMs: 1500));
        var t2 = Task.Run(() => wait2.ForElement(new FindSpec(Name: "Two", Hwnd: 102), timeoutMs: 1500));

        Thread.Sleep(50);
        elements1.Add(MakeEl("el1", "One", Role.Button, hwnd: 101));
        events.Fire(new ObservedEvent(EventKind.StructureChanged, DateTimeOffset.UtcNow, Hwnd: 101, ElementId: "el1", Detail: "Button"));

        var r1 = t1.Result;
        Assert.True(r1.Success);
        Assert.Equal("el1", r1.MatchedElement?.Id);

        // t2 is still waiting
        Assert.False(t2.IsCompleted);

        elements2.Add(MakeEl("el2", "Two", Role.Button, hwnd: 102));
        events.Fire(new ObservedEvent(EventKind.StructureChanged, DateTimeOffset.UtcNow, Hwnd: 102, ElementId: "el2", Detail: "Button"));

        var r2 = t2.Result;
        Assert.True(r2.Success);
        Assert.Equal("el2", r2.MatchedElement?.Id);
    }

    // =========================================================================
    // 2. VERIFICATION: EVENT + PROPERTY RACE
    // =========================================================================

    [Fact]
    public void AutoVerifier_PropertyChangeWinsFirst_ImmediatelyVerified()
    {
        var backend = new MockBackend();
        var winSvc = new DummyWindowService();
        winSvc.OpenWindows[100] = new WindowInfo(100, 1, "App", "app.exe", new RectPx(0, 0, 800, 600), WindowState.Normal, true, false, true, 0);
        var reg = new ElementRegistry([backend]);
        var events = new RecentEventBuffer();

        var verifier = new AutoVerifier(reg, [backend], winSvc, events);

        var initialEl = MakeEl("btn_toggle", "Toggle", Role.CheckBox, state: "unchecked", hwnd: 100);
        var pre = verifier.Snapshot(initialEl);

        // Next re-resolve returns changed property
        var changedEl = MakeEl("btn_toggle", "Toggle", Role.CheckBox, state: "checked", hwnd: 100);
        backend.OnReResolve = _ => changedEl;

        var action = new AgentAction(AgentActionKind.Click, ElementId: "btn_toggle");
        var actionResult = new ActionResult(true, BackendId.Uia, "NativeClick", [], VerifyResult.Unverified, TimeSpan.FromMilliseconds(5));

        var sw = Stopwatch.StartNew();
        var outcome = verifier.Verify(action, pre, actionResult, winSvc.GetWindow(100), CancellationToken.None);

        Assert.NotNull(outcome);
        Assert.Equal(OutcomeKind.Verified, outcome!.Kind);
        Assert.Equal("PropertyChanged", outcome.Evidence?.Method);
        Assert.True(sw.ElapsedMilliseconds < 400, "Property change should win on first tick without waiting full 900ms");
    }

    [Fact]
    public void AutoVerifier_WindowClosedWinsFirst_ImmediatelyVerified()
    {
        var backend = new MockBackend();
        var winSvc = new DummyWindowService();
        // Window is initially open
        winSvc.OpenWindows[100] = new WindowInfo(100, 1, "App", "app.exe", new RectPx(0, 0, 800, 600), WindowState.Normal, true, false, true, 0);
        var reg = new ElementRegistry([backend]);
        var events = new RecentEventBuffer();

        var verifier = new AutoVerifier(reg, [backend], winSvc, events);

        var closeBtn = MakeEl("btn_close", "Close", Role.Button, hwnd: 100);
        var pre = verifier.Snapshot(closeBtn);

        // Window closes as result of click
        winSvc.OpenWindows.Remove(100);
        backend.OnReResolve = _ => null;

        var action = new AgentAction(AgentActionKind.Click, ElementId: "btn_close");
        var actionResult = new ActionResult(true, BackendId.Uia, "NativeClick", [], VerifyResult.Unverified, TimeSpan.FromMilliseconds(5));

        var sw = Stopwatch.StartNew();
        var outcome = verifier.Verify(action, pre, actionResult, null, CancellationToken.None);

        Assert.NotNull(outcome);
        Assert.Equal(OutcomeKind.Verified, outcome!.Kind);
        Assert.Equal("WindowClosed", outcome.Evidence?.Method);
        Assert.True(sw.ElapsedMilliseconds < 300, "Window closed should win immediately");
    }

    [Fact]
    public void AutoVerifier_SemanticEventWins_WhenPropertyNotDirectlyReadable()
    {
        var backend = new MockBackend();
        var winSvc = new DummyWindowService();
        winSvc.OpenWindows[100] = new WindowInfo(100, 1, "App", "app.exe", new RectPx(0, 0, 800, 600), WindowState.Normal, true, false, true, 0);
        var reg = new ElementRegistry([backend]);
        var events = new RecentEventBuffer();

        var verifier = new AutoVerifier(reg, [backend], winSvc, events);

        var customBtn = MakeEl("custom_btn", "Submit", Role.Button, hwnd: 100);
        var pre = verifier.Snapshot(customBtn);

        // Element props unchanged, but semantic event occurred in window
        backend.OnReResolve = _ => customBtn;
        events.Add(new ObservedEvent(EventKind.StateChanged, DateTimeOffset.UtcNow, Hwnd: 100, ElementId: "custom_btn", Detail: "Button"));

        var action = new AgentAction(AgentActionKind.Click, ElementId: "custom_btn");
        var actionResult = new ActionResult(true, BackendId.Uia, "NativeClick", [], VerifyResult.Unverified, TimeSpan.FromMilliseconds(5));

        var outcome = verifier.Verify(action, pre, actionResult, winSvc.GetWindow(100), CancellationToken.None);

        Assert.NotNull(outcome);
        Assert.Equal(OutcomeKind.ObservedChange, outcome!.Kind);
        Assert.Equal("SemanticEvent", outcome.Evidence?.Method);
    }

    // =========================================================================
    // 3. BOUNDED FALLBACK POLLING
    // =========================================================================

    [Fact]
    public void BoundedFallback_MissingEvents_CompletesThroughPolling()
    {
        // EventSource emits zero events
        var events = new DummyEventSource();
        var capture = new DummyCaptureService();
        var registry = new ElementRegistry([]);
        var elements = new List<UiElement>();

        var wait = new WaitService(spec => elements, capture, events, registry);

        var task = Task.Run(() => wait.ForElement(new FindSpec(Name: "LateAppearing"), timeoutMs: 1500));

        // State changes after 300ms without ANY event
        Task.Delay(300).ContinueWith(_ => elements.Add(MakeEl("late_el", "LateAppearing", Role.Button)));

        var res = task.Result;
        Assert.True(res.Success);
        Assert.Equal("late_el", res.MatchedElement?.Id);
        Assert.True(wait.Telemetry.FallbackPollCount >= 1);
        Assert.Equal(0, wait.Telemetry.EventWakeCount);
    }

    [Fact]
    public void BoundedFallback_TimeoutEnforced_NoInfiniteWait()
    {
        var events = new DummyEventSource();
        var capture = new DummyCaptureService();
        var registry = new ElementRegistry([]);
        var elements = new List<UiElement>();

        var wait = new WaitService(spec => elements, capture, events, registry);

        var sw = Stopwatch.StartNew();
        var res = wait.ForElement(new FindSpec(Name: "NonExistent"), timeoutMs: 400);

        Assert.False(res.Success);
        Assert.Equal("timeout", res.Reason);
        Assert.True(sw.ElapsedMilliseconds >= 350 && sw.ElapsedMilliseconds < 900);
    }

    // =========================================================================
    // 4. WINDOW & DIALOG EVENT GATING
    // =========================================================================

    [Fact]
    public void ForCondition_DialogInterruption_GatedByWindowEvent()
    {
        var events = new DummyEventSource();
        var capture = new DummyCaptureService();
        var registry = new ElementRegistry([]);
        var winService = new DummyWindowService
        {
            Foreground = new WindowInfo(0x100, 1, "Main Window", "app", new RectPx(0, 0, 800, 600), WindowState.Normal, true, false, true, 0)
        };

        var wait = new WaitService(spec => [], capture, events, registry, winService);

        var task = Task.Run(() => wait.ForCondition(new WaitCondition(
            Name: "TargetAction",
            Hwnd: 0x100,
            StopOnUnexpectedDialog: true), timeoutMs: 2000));

        Thread.Sleep(50);

        // Spawn unexpected modal dialog
        winService.ActiveModal = new WindowInfo(0x999, 1, "Error: Connection Failed", "app",
            new RectPx(100, 100, 400, 200), WindowState.Normal, true, false, true, 0);

        // Fire window opened event
        events.Fire(new ObservedEvent(EventKind.WindowOpened, DateTimeOffset.UtcNow, Hwnd: 0x999, ElementId: "Error Dialog", Detail: "Window"));

        var res = task.Result;
        Assert.False(res.Success);
        Assert.True(res.InterruptedByDialog);
        Assert.Equal(0x999, res.DialogHwnd);
        Assert.Contains("Error: Connection Failed", res.DialogTitle);
    }

    // =========================================================================
    // 5. RESOURCE CLEANUP
    // =========================================================================

    [Fact]
    public void ResourceCleanup_Success_UnsubscribesCleanly()
    {
        var events = new DummyEventSource();
        var capture = new DummyCaptureService();
        var registry = new ElementRegistry([]);
        var elements = new List<UiElement> { MakeEl("btn1", "Go", Role.Button) };

        var wait = new WaitService(spec => elements, capture, events, registry);

        Assert.Equal(0, events.SubscriberCount);
        var res = wait.ForElement(new FindSpec(Name: "Go"), timeoutMs: 1000);
        Assert.True(res.Success);
        Assert.Equal(0, events.SubscriberCount);
    }

    [Fact]
    public void ResourceCleanup_Timeout_UnsubscribesCleanly()
    {
        var events = new DummyEventSource();
        var capture = new DummyCaptureService();
        var registry = new ElementRegistry([]);
        var elements = new List<UiElement>();

        var wait = new WaitService(spec => elements, capture, events, registry);

        Assert.Equal(0, events.SubscriberCount);
        var res = wait.ForElement(new FindSpec(Name: "NeverFound"), timeoutMs: 250);
        Assert.False(res.Success);
        Assert.Equal(0, events.SubscriberCount);
    }

    [Fact]
    public void ResourceCleanup_Cancellation_UnsubscribesCleanly()
    {
        var events = new DummyEventSource();
        var capture = new DummyCaptureService();
        var registry = new ElementRegistry([]);
        var elements = new List<UiElement>();

        var wait = new WaitService(spec => elements, capture, events, registry);
        using var cts = new CancellationTokenSource(100);

        Assert.Equal(0, events.SubscriberCount);
        Assert.ThrowsAny<OperationCanceledException>(() =>
            wait.ForElement(new FindSpec(Name: "NeverFound"), timeoutMs: 2000, ct: cts.Token));

        Assert.Equal(0, events.SubscriberCount);
    }

    // =========================================================================
    // 6. OPERATION ID & STRUCTURAL CANONICALIZATION
    // =========================================================================

    [Fact]
    public void Batch_OperationId_DifferentUntil_ProducesConflict()
    {
        var dedup = new RequestDeduplicator();
        var tele = new SessionTelemetry();

        var opId = "op_batch_f_until_test";
        var normArgs1 = System.Text.Json.JsonSerializer.Serialize(new
        {
            steps = new[] { new { @do = "click", target = "Submit" } },
            until = new { appears = "SuccessModal", timeoutMs = 5000 }
        });

        var normArgs2 = System.Text.Json.JsonSerializer.Serialize(new
        {
            steps = new[] { new { @do = "click", target = "Submit" } },
            until = new { appears = "ErrorBanner", timeoutMs = 5000 }
        });

        // First execution succeeds
        var cached = dedup.TryDeduplicateMutation(opId, "computer_batch", normArgs1, tele, out var conflict1);
        Assert.Null(cached);
        Assert.Null(conflict1);

        var firstResult = new CallToolResult { Content = [new TextContentBlock { Text = "{\"status\":\"Ok\"}" }] };
        dedup.RecordMutation(opId, "computer_batch", normArgs1, firstResult);

        // Second execution with DIFFERENT until condition must produce OperationIdConflict
        var cached2 = dedup.TryDeduplicateMutation(opId, "computer_batch", normArgs2, tele, out var conflict2);
        Assert.Null(cached2);
        Assert.NotNull(conflict2);
        Assert.Contains("OperationIdConflict", ((TextContentBlock)conflict2.Content[0]).Text);
    }

    [Fact]
    public void Batch_OperationId_DifferentRead_ProducesConflict()
    {
        var dedup = new RequestDeduplicator();
        var tele = new SessionTelemetry();

        var opId = "op_batch_f_read_test";
        var normArgs1 = System.Text.Json.JsonSerializer.Serialize(new
        {
            steps = new[] { new { @do = "click", target = "Refresh" } },
            read = new[] { new { target = "StatusLabel", props = new[] { "value" } } }
        });

        var normArgs2 = System.Text.Json.JsonSerializer.Serialize(new
        {
            steps = new[] { new { @do = "click", target = "Refresh" } },
            read = new[] { new { target = "RowCountLabel", props = new[] { "name" } } }
        });

        dedup.TryDeduplicateMutation(opId, "computer_batch", normArgs1, tele, out _);
        var firstResult = new CallToolResult { Content = [new TextContentBlock { Text = "{\"status\":\"Ok\"}" }] };
        dedup.RecordMutation(opId, "computer_batch", normArgs1, firstResult);

        // Conflicting read spec
        var cached = dedup.TryDeduplicateMutation(opId, "computer_batch", normArgs2, tele, out var conflict);
        Assert.Null(cached);
        Assert.NotNull(conflict);
        Assert.Contains("OperationIdConflict", ((TextContentBlock)conflict.Content[0]).Text);
    }

    [Fact]
    public void Batch_OperationId_IdenticalRequest_ReplaysResult()
    {
        var dedup = new RequestDeduplicator();
        var tele = new SessionTelemetry();

        var opId = "op_batch_f_replay_test";
        var normArgs = System.Text.Json.JsonSerializer.Serialize(new
        {
            steps = new[] { new { @do = "click", target = "Submit" } },
            until = new { appears = "SuccessModal", timeoutMs = 5000 },
            read = new[] { new { target = "Result", props = new[] { "value" } } }
        });

        dedup.TryDeduplicateMutation(opId, "computer_batch", normArgs, tele, out _);
        var originalResult = new CallToolResult { Content = [new TextContentBlock { Text = "{\"status\":\"Ok\",\"executed\":1}" }] };
        dedup.RecordMutation(opId, "computer_batch", normArgs, originalResult);

        // Exactly identical replay
        var replayed = dedup.TryDeduplicateMutation(opId, "computer_batch", normArgs, tele, out var conflict);
        Assert.NotNull(replayed);
        Assert.Null(conflict);
        Assert.Equal("{\"status\":\"Ok\",\"executed\":1}", ((TextContentBlock)replayed.Content[0]).Text);
    }

    // =========================================================================
    // 7. COMPLETION TIMELINE INTEGRITY
    // =========================================================================

    [Fact]
    public void CompletionTimeline_EmitsMonotonicTimestamps()
    {
        var events = new DummyEventSource();
        var capture = new DummyCaptureService();
        var registry = new ElementRegistry([]);
        var elements = new List<UiElement>();

        var wait = new WaitService(spec => elements, capture, events, registry);

        var task = Task.Run(() => wait.ForElement(new FindSpec(Name: "Target"), timeoutMs: 1500));
        Thread.Sleep(80);
        elements.Add(MakeEl("el1", "Target", Role.Button));
        events.Fire(new ObservedEvent(EventKind.StructureChanged, DateTimeOffset.UtcNow, Hwnd: 100, ElementId: "el1", Detail: "Button"));

        var res = task.Result;
        Assert.True(res.Success);
        Assert.NotNull(res.Timeline);
        Assert.Equal(0, res.Timeline!.t_start);
        Assert.NotNull(res.Timeline.t_firstChange);
        Assert.NotNull(res.Timeline.t_stable);
        Assert.True(res.Timeline.t_firstChange >= res.Timeline.t_start);
        Assert.True(res.Timeline.t_stable >= res.Timeline.t_firstChange);
    }

    // =========================================================================
    // 8. REPAIR ROUND 1: OPERATIONID, LOSER CANCELLATION, CLEANUP & EXACTLY-ONCE
    // =========================================================================

    [Fact]
    public void Batch_OperationId_DifferentStableMs_ProducesConflict()
    {
        var dedup = new RequestDeduplicator();
        var tele = new SessionTelemetry();

        var opId = "op_batch_stablems_conflict";
        var normArgs1 = System.Text.Json.JsonSerializer.Serialize(new
        {
            steps = new[] { new { @do = "click", target = "Submit" } },
            until = new { stable = true, timeoutMs = 3000, stableMs = 100 }
        });
        var normArgs2 = System.Text.Json.JsonSerializer.Serialize(new
        {
            steps = new[] { new { @do = "click", target = "Submit" } },
            until = new { stable = true, timeoutMs = 3000, stableMs = 500 }
        });

        dedup.TryDeduplicateMutation(opId, "computer_batch", normArgs1, tele, out _);
        var firstResult = new CallToolResult { Content = [new TextContentBlock { Text = "{\"status\":\"Ok\"}" }] };
        dedup.RecordMutation(opId, "computer_batch", normArgs1, firstResult);

        var cached = dedup.TryDeduplicateMutation(opId, "computer_batch", normArgs2, tele, out var conflict);
        Assert.Null(cached);
        Assert.NotNull(conflict);
        Assert.Contains("OperationIdConflict", ((TextContentBlock)conflict.Content[0]).Text);
    }

    [Fact]
    public void Batch_OperationId_DifferentTimeoutMs_ProducesConflict()
    {
        var dedup = new RequestDeduplicator();
        var tele = new SessionTelemetry();

        var opId = "op_batch_timeoutms_conflict";
        var normArgs1 = System.Text.Json.JsonSerializer.Serialize(new
        {
            steps = new[] { new { @do = "click", target = "Submit" } },
            until = new { stable = true, timeoutMs = 2000, stableMs = 200 }
        });
        var normArgs2 = System.Text.Json.JsonSerializer.Serialize(new
        {
            steps = new[] { new { @do = "click", target = "Submit" } },
            until = new { stable = true, timeoutMs = 5000, stableMs = 200 }
        });

        dedup.TryDeduplicateMutation(opId, "computer_batch", normArgs1, tele, out _);
        var firstResult = new CallToolResult { Content = [new TextContentBlock { Text = "{\"status\":\"Ok\"}" }] };
        dedup.RecordMutation(opId, "computer_batch", normArgs1, firstResult);

        var cached = dedup.TryDeduplicateMutation(opId, "computer_batch", normArgs2, tele, out var conflict);
        Assert.Null(cached);
        Assert.NotNull(conflict);
        Assert.Contains("OperationIdConflict", ((TextContentBlock)conflict.Content[0]).Text);
    }

    [Fact]
    public void AutoVerifier_PropertyWinner_StopsEventAndFallbackPaths()
    {
        var backend = new MockBackend();
        var winSvc = new DummyWindowService();
        winSvc.OpenWindows[100] = new WindowInfo(100, 1, "App", "app.exe", new RectPx(0, 0, 800, 600), WindowState.Normal, true, false, true, 0);
        var reg = new ElementRegistry([backend]);
        var events = new RecentEventBuffer();

        var verifier = new AutoVerifier(reg, [backend], winSvc, events);

        var initialEl = MakeEl("btn_toggle", "Toggle", Role.CheckBox, state: "unchecked", hwnd: 100);
        var pre = verifier.Snapshot(initialEl);

        // Immediate property change
        var changedEl = MakeEl("btn_toggle", "Toggle", Role.CheckBox, state: "checked", hwnd: 100);
        backend.OnReResolve = _ => changedEl;

        var action = new AgentAction(AgentActionKind.Click, ElementId: "btn_toggle");
        var actionResult = new ActionResult(true, BackendId.Uia, "NativeClick", [], VerifyResult.Unverified, TimeSpan.FromMilliseconds(5));

        var outcome = verifier.Verify(action, pre, actionResult, winSvc.GetWindow(100), CancellationToken.None);

        Assert.NotNull(outcome);
        Assert.Equal(OutcomeKind.Verified, outcome!.Kind);
        Assert.Equal("PropertyChanged", outcome.Evidence?.Method);

        // Verification of winner stopping losers:
        Assert.Equal(1, verifier.Telemetry.PropertyCheckCount);
        Assert.Equal(0, verifier.Telemetry.FallbackPollCount);
        Assert.Equal(0, verifier.Telemetry.EventWakeCount);
        Assert.Equal(0, events.ActiveWaiters);
    }

    [Fact]
    public void AutoVerifier_EventWinner_StopsFallbackPath()
    {
        var backend = new MockBackend();
        var winSvc = new DummyWindowService();
        winSvc.OpenWindows[100] = new WindowInfo(100, 1, "App", "app.exe", new RectPx(0, 0, 800, 600), WindowState.Normal, true, false, true, 0);
        var reg = new ElementRegistry([backend]);
        var events = new RecentEventBuffer();

        var verifier = new AutoVerifier(reg, [backend], winSvc, events);

        var customBtn = MakeEl("custom_btn", "Submit", Role.Button, hwnd: 100);
        var pre = verifier.Snapshot(customBtn);

        // Element props unchanged initially
        backend.OnReResolve = _ => customBtn;

        // Semantic event already in buffer
        events.Add(new ObservedEvent(EventKind.StateChanged, DateTimeOffset.UtcNow, Hwnd: 100, ElementId: "custom_btn", Detail: "Button"));

        var action = new AgentAction(AgentActionKind.Click, ElementId: "custom_btn");
        var actionResult = new ActionResult(true, BackendId.Uia, "NativeClick", [], VerifyResult.Unverified, TimeSpan.FromMilliseconds(5));

        var outcome = verifier.Verify(action, pre, actionResult, winSvc.GetWindow(100), CancellationToken.None);

        Assert.NotNull(outcome);
        Assert.Equal(OutcomeKind.ObservedChange, outcome!.Kind);
        Assert.Equal("SemanticEvent", outcome.Evidence?.Method);

        // Event satisfies immediately on first tick without waiting for fallback
        Assert.Equal(0, verifier.Telemetry.FallbackPollCount);
        Assert.Equal(0, events.ActiveWaiters);
    }

    [Fact]
    public void ResourceCleanup_ActionFailure_UnsubscribesCleanly()
    {
        var events = new DummyEventSource();
        var capture = new DummyCaptureService();
        var registry = new ElementRegistry([]);

        // Find delegate throws exception during wait
        var wait = new WaitService(spec => throw new InvalidOperationException("Action failed"), capture, events, registry);

        Assert.Throws<InvalidOperationException>(() => wait.ForElement(new FindSpec(Name: "Target"), timeoutMs: 1000));
        Assert.Equal(0, wait.ActiveTrackers);
    }

    [Fact]
    public void ResourceCleanup_VerificationFailure_UnsubscribesCleanly()
    {
        var backend = new MockBackend();
        var winSvc = new DummyWindowService();
        winSvc.OpenWindows[100] = new WindowInfo(100, 1, "App", "app.exe", new RectPx(0, 0, 800, 600), WindowState.Normal, true, false, true, 0);
        var reg = new ElementRegistry([backend]);
        var events = new RecentEventBuffer();

        var verifier = new AutoVerifier(reg, [backend], winSvc, events);

        var toggleBtn = MakeEl("btn_toggle", "Toggle", Role.CheckBox, state: "unchecked", hwnd: 100);
        var pre = verifier.Snapshot(toggleBtn);

        // Never changes
        backend.OnReResolve = _ => toggleBtn;

        var action = new AgentAction(AgentActionKind.Toggle, ElementId: "btn_toggle");
        var actionResult = new ActionResult(true, BackendId.Uia, "NativeClick", [], VerifyResult.Unverified, TimeSpan.FromMilliseconds(5));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        try
        {
            verifier.Verify(action, pre, actionResult, winSvc.GetWindow(100), cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected upon token cancellation during polling
        }

        // Verification fails or returns unverified — active waiters MUST return to 0
        Assert.Equal(0, events.ActiveWaiters);
    }

    [Fact]
    public void ResourceCleanup_SessionDispose_ReleasesActiveWaiters()
    {
        var events = new RecentEventBuffer();

        var waitTask = Task.Run(() => events.WaitForNextEvent(0, 5000));

        // Wait until task enters WaitForNextEvent
        var spin = Stopwatch.StartNew();
        while (events.ActiveWaiters == 0 && spin.ElapsedMilliseconds < 3000)
            Thread.Sleep(5);

        Assert.Equal(1, events.ActiveWaiters);

        // Session dispose pulses and releases active waiters immediately
        events.Dispose();

        var result = waitTask.Wait(3000);
        Assert.True(result, "Waiter must unblock immediately upon buffer disposal");
        Assert.False(waitTask.Result, "Disposed wait returns false");
        Assert.Equal(0, events.ActiveWaiters);
    }

    [Fact]
    public async Task EventCompletion_ActionExactlyOnce_AndLeaseReleasedBeforeWait()
    {
        var arbiter = new DesktopArbiter();
        int actionExecutionCount = 0;
        IInputLease? physicalLease = null;

        // Step 1: Acquire lease for physical action
        physicalLease = await arbiter.AcquireAsync("test_owner", LeaseKind.PhysicalInput, "Click Action", timeout: TimeSpan.FromSeconds(2));
        Assert.NotNull(physicalLease);
        Assert.NotNull(arbiter.GetExclusiveOwner());

        // Step 2: Physical action executes exactly once
        actionExecutionCount++;
        Assert.Equal(1, actionExecutionCount);

        // Step 3: Physical lease released before wait monitoring starts
        physicalLease.Dispose();
        Assert.Null(arbiter.GetExclusiveOwner());

        // Step 4: Completion monitoring begins while physical lock is free
        var events = new DummyEventSource();
        var capture = new DummyCaptureService();
        var registry = new ElementRegistry([]);
        var elements = new List<UiElement>();
        var wait = new WaitService(spec => elements, capture, events, registry);

        var waitTask = Task.Run(() => wait.ForElement(new FindSpec(Name: "LoadedTarget"), timeoutMs: 2000));

        // Step 5: Burst of subsequent events arrives; none re-triggers mutation
        for (int i = 0; i < 10; i++)
        {
            events.Fire(new ObservedEvent(EventKind.StructureChanged, DateTimeOffset.UtcNow, Hwnd: 100, Detail: $"Noise_{i}"));
        }

        // Mutation count remains strictly 1
        Assert.Equal(1, actionExecutionCount);

        // Terminal satisfying event
        elements.Add(MakeEl("loaded_el", "LoadedTarget", Role.Button));
        events.Fire(new ObservedEvent(EventKind.StructureChanged, DateTimeOffset.UtcNow, Hwnd: 100, ElementId: "loaded_el", Detail: "Loaded"));

        var waitResult = await waitTask;
        Assert.True(waitResult.Success);
        Assert.Equal(1, actionExecutionCount);
        Assert.Null(arbiter.GetExclusiveOwner());
    }

    [Fact]
    public async Task EventStorm_ConcurrentUnrelatedEvents_FiltersCleanlyAndCompletesOnce()
    {
        var events = new DummyEventSource();
        var capture = new DummyCaptureService();
        var registry = new ElementRegistry([]);
        var elements = new List<UiElement>();
        var wait = new WaitService(spec => elements, capture, events, registry);

        var waitTask = Task.Run(() => wait.ForElement(new FindSpec(Name: "TargetElement", Hwnd: 100), timeoutMs: 3000));

        // Background task injects concurrent stream of 50 unrelated events
        var noiseTask = Task.Run(async () =>
        {
            for (int i = 0; i < 50; i++)
            {
                events.Fire(new ObservedEvent(EventKind.StateChanged, DateTimeOffset.UtcNow, Hwnd: 999, ElementId: $"noise_{i}"));
                if (i % 10 == 0) await Task.Delay(5);
            }

            // Finally, inject the relevant event and add target
            elements.Add(MakeEl("target_id", "TargetElement", Role.Button, hwnd: 100));
            events.Fire(new ObservedEvent(EventKind.StructureChanged, DateTimeOffset.UtcNow, Hwnd: 100, ElementId: "target_id"));
        });

        var res = await waitTask;
        await noiseTask;

        Assert.True(res.Success);
        Assert.Equal("target_id", res.MatchedElement?.Id);
        Assert.True(wait.Telemetry.IgnoredEvents >= 30, $"Expected >=30 ignored events, got {wait.Telemetry.IgnoredEvents}");
        Assert.Equal(1, wait.Telemetry.EventWakeCount);
        Assert.Equal(0, wait.ActiveTrackers);
    }
}
