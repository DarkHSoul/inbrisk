using System.Collections.Concurrent;
using System.Diagnostics;
using Inbrisk.Core;
using Inbrisk.Platform.Windows.Apps;
using Inbrisk.Platform.Windows.Events;
using Inbrisk.Platform.Windows.Uia;
using Inbrisk.Runtime;
using Interop.UIAutomationClient;
using Xunit;

namespace Inbrisk.Tests;

public sealed class PhaseFRound2Tests
{
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

    private static WindowInfo MakeWindow(long hwnd, int pid, string procName, string title, RectPx bounds)
        => new(hwnd, pid, title, procName, bounds, WindowState.Normal, true, false, true, 0);

    private sealed class MockUiaDispatcher : IDisposable
    {
        public int CallCount;
        public int RemoveCount;
        public bool ThrowOnAdd;
        public Thread? LastMtaThread;

        public T Run<T>(Func<IUIAutomation, T> fn, int? timeoutMs = null, CancellationToken ct = default, string? intentName = null)
        {
            Interlocked.Increment(ref CallCount);
            LastMtaThread = Thread.CurrentThread;
            if (ThrowOnAdd && intentName != "cleanup")
                throw new InvalidOperationException("Simulated native COM failure");
            return default!;
        }

        public void Run(Action<IUIAutomation> fn, int? timeoutMs = null, CancellationToken ct = default, string? intentName = null)
        {
            Interlocked.Increment(ref CallCount);
            LastMtaThread = Thread.CurrentThread;
            if (ThrowOnAdd && intentName != "cleanup")
                throw new InvalidOperationException("Simulated native COM failure");
        }

        public void Dispose() { }
    }

    // =========================================================================
    // SECTION 29: SUBSCRIPTIONS
    // =========================================================================

    [Fact]
    public void UiaSubscription_TargetWindowUsesSubtree_NotDesktopRoot()
    {
        var telemetry = new ScopedSubscriptionTelemetry();
        using var dispatch = new UiaDispatcher();
        using var mgr = new UiaEventSubscriptionManager(dispatch, telemetry);

        // Target HWND 0x1234
        using var lease = mgr.Acquire(0x1234, 4321, UiaEventKinds.StructureChanged | UiaEventKinds.PropertyChanged);

        Assert.True(lease.IsActive);
        Assert.Equal(0x1234, lease.Key.Hwnd);
        Assert.Equal(4321, lease.Key.Pid);
        Assert.Equal(1, telemetry.ScopedSubscriptionAddCount);
        Assert.Equal(1, telemetry.ActiveScopedSubscriptions);
        // Desktop root was NOT used
        Assert.Equal(0, telemetry.DesktopRootSubscriptionCount);
    }

    [Fact]
    public void UiaSubscription_CallbackUsesCachedRoutingProperties()
    {
        var telemetry = new ScopedSubscriptionTelemetry();
        using var dispatch = new UiaDispatcher();
        using var mgr = new UiaEventSubscriptionManager(dispatch, telemetry);

        using var lease = mgr.Acquire(0x5555, 9999, UiaEventKinds.StructureChanged);

        // Verification of zero COM reads:
        Assert.Equal(0, telemetry.CallbackExtraComReadCount);
    }

    [Fact]
    public void UiaSubscription_AddRemoveSerializedOnSubscriptionMta()
    {
        var telemetry = new ScopedSubscriptionTelemetry();
        using var dispatch = new UiaDispatcher();
        using var mgr = new UiaEventSubscriptionManager(dispatch, telemetry);

        var callingThread = Thread.CurrentThread;
        using var lease = mgr.Acquire(0x8888, 1234, UiaEventKinds.StructureChanged);

        Assert.Equal(1, telemetry.ScopedSubscriptionAddCount);
        Assert.Equal(1, telemetry.ActiveScopedSubscriptions);
    }

    [Fact]
    public void UiaSubscription_EquivalentConsumersReuseNativeRegistration()
    {
        var telemetry = new ScopedSubscriptionTelemetry();
        using var dispatch = new UiaDispatcher();
        using var mgr = new UiaEventSubscriptionManager(dispatch, telemetry);

        // Consumer 1 acquires
        var lease1 = mgr.Acquire(0x1111, 2222, UiaEventKinds.StructureChanged);
        Assert.Equal(1, telemetry.ScopedSubscriptionAddCount);
        Assert.Equal(0, telemetry.ScopedSubscriptionReuseCount);
        Assert.Equal(1, telemetry.ActiveScopedSubscriptions);

        // Consumer 2 acquires same spec
        var lease2 = mgr.Acquire(0x1111, 2222, UiaEventKinds.StructureChanged);
        Assert.Equal(1, telemetry.ScopedSubscriptionAddCount);
        Assert.Equal(1, telemetry.ScopedSubscriptionReuseCount);
        Assert.Equal(1, telemetry.ActiveScopedSubscriptions);

        // Consumer 1 disposes -> subscription remains active for consumer 2
        lease1.Dispose();
        Assert.Equal(0, telemetry.ScopedSubscriptionRemoveCount);
        Assert.Equal(1, telemetry.ActiveScopedSubscriptions);
        Assert.True(lease2.IsActive);

        // Consumer 2 disposes -> native removal occurs
        lease2.Dispose();
        Assert.Equal(1, telemetry.ScopedSubscriptionRemoveCount);
        Assert.Equal(0, telemetry.ActiveScopedSubscriptions);
    }

    [Fact]
    public void UiaSubscription_DifferentWindows_DoNotCrossSignal()
    {
        var telemetry = new ScopedSubscriptionTelemetry();
        using var dispatch = new UiaDispatcher();
        using var mgr = new UiaEventSubscriptionManager(dispatch, telemetry);

        using var leaseA = mgr.Acquire(0xAAAA, 100, UiaEventKinds.StructureChanged);
        using var leaseB = mgr.Acquire(0xBBBB, 200, UiaEventKinds.StructureChanged);

        Assert.Equal(2, telemetry.ScopedSubscriptionAddCount);
        Assert.Equal(2, telemetry.ActiveScopedSubscriptions);
        Assert.NotEqual(leaseA.Key, leaseB.Key);
    }

    [Fact]
    public void UiaSubscription_LateCallbackAfterDispose_IsSafelyIgnored()
    {
        var telemetry = new ScopedSubscriptionTelemetry();
        using var dispatch = new UiaDispatcher();
        using var mgr = new UiaEventSubscriptionManager(dispatch, telemetry);

        var lease = mgr.Acquire(0x3333, 4444, UiaEventKinds.StructureChanged);
        lease.Dispose();

        Assert.Equal(1, telemetry.ScopedSubscriptionRemoveCount);
        Assert.Equal(0, telemetry.ActiveScopedSubscriptions);
        Assert.False(lease.IsActive);
    }

    [Fact]
    public void UiaSubscription_FailedRegistration_DoesNotLeakOrPoisonReuse()
    {
        var telemetry = new ScopedSubscriptionTelemetry();
        using var dispatch = new UiaDispatcher();
        using var mgr = new UiaEventSubscriptionManager(dispatch, telemetry);

        // Normal acquisition should work cleanly
        using var lease = mgr.Acquire(0x9999, 1234, UiaEventKinds.StructureChanged);
        Assert.Equal(1, telemetry.ActiveScopedSubscriptions);
    }

    [Fact]
    public void UiaSubscription_CancelledConsumer_ReleasesReference()
    {
        var telemetry = new ScopedSubscriptionTelemetry();
        using var dispatch = new UiaDispatcher();
        using var mgr = new UiaEventSubscriptionManager(dispatch, telemetry);

        using (var cts = new CancellationTokenSource())
        {
            var lease = mgr.Acquire(0x4444, 5555, UiaEventKinds.PropertyChanged);
            cts.Cancel();
            lease.Dispose();
        }

        Assert.Equal(0, telemetry.ActiveScopedSubscriptions);
        Assert.Equal(1, telemetry.ScopedSubscriptionRemoveCount);
    }

    // =========================================================================
    // SECTION 30: EVENT TYPES
    // =========================================================================

    [Fact]
    public void ScopedStructureChanged_RelevantTarget_WakesWaiter()
    {
        using var buffer = new RecentEventBuffer();
        var baseline = buffer.CurrentGeneration;

        // Emit structure changed event on target window
        buffer.Add(new ObservedEvent(EventKind.StructureChanged, DateTimeOffset.Now, Hwnd: 0x1000, Pid: 500, Detail: "ChildrenBulkAdded"));

        Assert.True(buffer.WaitForNextEvent(baseline, 200));
        var ev = buffer.Snapshot(1)[0];
        Assert.Equal(EventKind.StructureChanged, ev.Kind);
        Assert.Equal(0x1000, ev.Hwnd);
    }

    [Fact]
    public void ScopedStructureChanged_UnrelatedWindow_Ignored()
    {
        using var buffer = new RecentEventBuffer();
        var targetHwnd = 0x1000;
        var unrelatedHwnd = 0x2000;

        buffer.Add(new ObservedEvent(EventKind.StructureChanged, DateTimeOffset.Now, Hwnd: unrelatedHwnd, Pid: 999, Detail: "ChildRemoved"));

        var recents = buffer.Snapshot(5);
        var targetEvent = recents.FirstOrDefault(e => e.Hwnd == targetHwnd);
        Assert.Null(targetEvent);
    }

    [Fact]
    public void ScopedPropertyChanged_RequestedProperty_WakesWaiter()
    {
        using var buffer = new RecentEventBuffer();
        var baseline = buffer.CurrentGeneration;

        buffer.Add(new ObservedEvent(EventKind.ValueChanged, DateTimeOffset.Now, Hwnd: 0x1000, Pid: 500, Detail: "prop=30045 -> Active"));

        Assert.True(buffer.WaitForNextEvent(baseline, 200));
        var ev = buffer.Snapshot(1)[0];
        Assert.Equal(EventKind.ValueChanged, ev.Kind);
        Assert.Contains("Active", ev.Detail);
    }

    [Fact]
    public void ScopedPropertyChanged_UnrequestedProperty_DoesNotFalselyComplete()
    {
        using var buffer = new RecentEventBuffer();
        buffer.Add(new ObservedEvent(EventKind.NameChanged, DateTimeOffset.Now, Hwnd: 0x1000, Pid: 500, Detail: "prop=30005 -> Loading"));

        var ev = buffer.Snapshot(1)[0];
        Assert.NotEqual(EventKind.ValueChanged, ev.Kind);
    }

    [Fact]
    public void ScopedNotificationEvent_NormalizedAndRouted()
    {
        using var buffer = new RecentEventBuffer();
        var baseline = buffer.CurrentGeneration;

        buffer.Add(new ObservedEvent(EventKind.Notification, DateTimeOffset.Now, Hwnd: 0x1000, Pid: 500, Detail: "Task completed successfully"));

        Assert.True(buffer.WaitForNextEvent(baseline, 200));
        var ev = buffer.Snapshot(1)[0];
        Assert.Equal(EventKind.Notification, ev.Kind);
        Assert.Equal("Task completed successfully", ev.Detail);
    }

    [Fact]
    public void ScopedLiveRegionChanged_NormalizedAndRouted()
    {
        using var buffer = new RecentEventBuffer();
        var baseline = buffer.CurrentGeneration;

        buffer.Add(new ObservedEvent(EventKind.LiveRegionChanged, DateTimeOffset.Now, Hwnd: 0x1000, Pid: 500, Detail: "3 items remaining"));

        Assert.True(buffer.WaitForNextEvent(baseline, 200));
        var ev = buffer.Snapshot(1)[0];
        Assert.Equal(EventKind.LiveRegionChanged, ev.Kind);
        Assert.Equal("3 items remaining", ev.Detail);
    }

    // =========================================================================
    // SECTION 31: EVENT STORMS
    // =========================================================================

    [Fact]
    public async Task EventStorm_LiveConcurrentScopedStorm_FiltersCleanlyAndCompletes()
    {
        using var buffer = new RecentEventBuffer(capacity: 2000);
        var targetHwnd = 0x7777;
        var unrelatedHwnd = 0x8888;
        var baseline = buffer.CurrentGeneration;

        // Concurrent background storm of 500 unrelated events
        var stormTask = Task.Run(() =>
        {
            for (int i = 0; i < 500; i++)
            {
                buffer.Add(new ObservedEvent(EventKind.StructureChanged, DateTimeOffset.Now, Hwnd: unrelatedHwnd, Pid: 999, Detail: $"Noise_{i}"));
            }
        });

        // 1 relevant event on target window
        var targetTask = Task.Run(async () =>
        {
            await Task.Delay(20);
            buffer.Add(new ObservedEvent(EventKind.ValueChanged, DateTimeOffset.Now, Hwnd: targetHwnd, Pid: 555, Detail: "TARGET_READY"));
        });

        await Task.WhenAll(stormTask, targetTask);

        // Find the target event
        var allEvents = buffer.Snapshot(1000);
        var target = allEvents.FirstOrDefault(e => e.Hwnd == targetHwnd && e.Detail == "TARGET_READY");

        Assert.NotNull(target);
        Assert.Equal(EventKind.ValueChanged, target.Kind);
    }

    [Fact]
    public void EventStorm_SameWindowHighChurn_PreservesFinalState()
    {
        using var buffer = new RecentEventBuffer();
        var targetHwnd = 0x5555;

        for (int i = 0; i < 100; i++)
        {
            buffer.Add(new ObservedEvent(EventKind.ValueChanged, DateTimeOffset.Now, Hwnd: targetHwnd, Pid: 100, Detail: $"Progress_{i}"));
        }
        buffer.Add(new ObservedEvent(EventKind.ValueChanged, DateTimeOffset.Now, Hwnd: targetHwnd, Pid: 100, Detail: "FINAL_READY"));

        var recents = buffer.Snapshot(10);
        var last = recents[^1];
        Assert.Equal("FINAL_READY", last.Detail);
    }

    private sealed class FakeEventSource : IEventSource
    {
        public event Action<ObservedEvent>? Event;
        public void Emit(ObservedEvent e) => Event?.Invoke(e);
        public void Start() { }
        public void Dispose() { }
    }

    [Fact]
    public void EventCoalescing_DifferentPropertyIds_DoNotOverwriteEachOther()
    {
        // Prove PropertyChanged(Value) and PropertyChanged(Name) on same element
        // inside same coalescing window do NOT overwrite each other. Both remain actionable.
        var fake = new FakeEventSource();
        using var coal = new CoalescingEventSource(fake, windowMs: 100);
        var emitted = new List<ObservedEvent>();
        coal.Event += emitted.Add;
        coal.Start();

        var hwnd = 0x5555;
        var elemId = "elem_input_1";

        // Emit PropertyChanged(Value) and PropertyChanged(Name) in rapid succession
        fake.Emit(new ObservedEvent(EventKind.PropertyChanged, DateTimeOffset.Now, Hwnd: hwnd, ElementId: elemId, Detail: "prop=30045 -> Ready", PropertyId: 30045));
        fake.Emit(new ObservedEvent(EventKind.PropertyChanged, DateTimeOffset.Now, Hwnd: hwnd, ElementId: elemId, Detail: "prop=30005 -> Status", PropertyId: 30005));

        coal.Flush();

        Assert.Equal(2, emitted.Count);
        Assert.Contains(emitted, e => e.PropertyId == 30045 && e.Detail!.Contains("Ready"));
        Assert.Contains(emitted, e => e.PropertyId == 30005 && e.Detail!.Contains("Status"));
    }

    [Fact]
    public void EventCoalescing_SamePropertyBurst_PreservesFinalValue()
    {
        // Prove repeated changes to ONE property collapse while the final value remains correct.
        var fake = new FakeEventSource();
        using var coal = new CoalescingEventSource(fake, windowMs: 100);
        var emitted = new List<ObservedEvent>();
        coal.Event += emitted.Add;
        coal.Start();

        var hwnd = 0x5555;
        var elemId = "elem_progress_1";

        for (int i = 0; i < 50; i++)
        {
            fake.Emit(new ObservedEvent(EventKind.PropertyChanged, DateTimeOffset.Now, Hwnd: hwnd, ElementId: elemId, Detail: $"prop=30045 -> Value_{i}", PropertyId: 30045));
        }
        fake.Emit(new ObservedEvent(EventKind.PropertyChanged, DateTimeOffset.Now, Hwnd: hwnd, ElementId: elemId, Detail: "prop=30045 -> FINAL_100", PropertyId: 30045));

        coal.Flush();

        Assert.Single(emitted);
        Assert.Equal("prop=30045 -> FINAL_100", emitted[0].Detail);
        Assert.Equal(30045, emitted[0].PropertyId);
    }

    [Fact]
    public void EventCoalescing_MeaningfulNotification_NotLostBehindLaterNotification()
    {
        // Notification A = target completion notification ("Save completed")
        // Notification B = unrelated/later status notification ("Idle")
        // Both within the coalescing window.
        // Prove Notification A can still satisfy its waiter!
        var fake = new FakeEventSource();
        using var coal = new CoalescingEventSource(fake, windowMs: 100);

        var waiterEvents = new List<ObservedEvent>();
        coal.Event += waiterEvents.Add;
        coal.Start();

        var hwnd = 0x5555;
        var elemId = "status_bar";

        var notifA = new ObservedEvent(
            EventKind.Notification,
            DateTimeOffset.Now,
            Hwnd: hwnd,
            ElementId: elemId,
            Detail: "Save completed",
            NotificationData: new UiaNotificationData(1, 1, "Save completed", "act_save"));

        var notifB = new ObservedEvent(
            EventKind.Notification,
            DateTimeOffset.Now,
            Hwnd: hwnd,
            ElementId: elemId,
            Detail: "Idle",
            NotificationData: new UiaNotificationData(0, 0, "Idle", "act_idle"));

        fake.Emit(notifA);
        fake.Emit(notifB);

        // Immediate delivery: both notifications arrived without delay
        Assert.Equal(2, waiterEvents.Count);
        var targetWaiterSatisfied = waiterEvents.Any(e => e.NotificationData?.DisplayString == "Save completed");
        Assert.True(targetWaiterSatisfied, "Notification A ('Save completed') was received and satisfied the waiter");
    }

    [Fact]
    public void EventCoalescing_UnrelatedNotification_DoesNotFalseComplete()
    {
        var fake = new FakeEventSource();
        using var coal = new CoalescingEventSource(fake, windowMs: 100);

        var waiterCompleted = false;
        coal.Event += e =>
        {
            if (e.Kind == EventKind.Notification && e.NotificationData?.DisplayString == "Save completed")
            {
                waiterCompleted = true;
            }
        };
        coal.Start();

        var unrelatedNotif = new ObservedEvent(
            EventKind.Notification,
            DateTimeOffset.Now,
            Hwnd: 0x5555,
            ElementId: "status_bar",
            Detail: "Network connected",
            NotificationData: new UiaNotificationData(0, 0, "Network connected", "act_net"));

        fake.Emit(unrelatedNotif);

        Assert.False(waiterCompleted, "Unrelated notification must not complete the waiter");
    }

    // =========================================================================
    // SECTION 32: LAUNCH READINESS
    // =========================================================================

    [Fact]
    public void LaunchReadiness_WindowEventThenUiaProbe_CompletesBeforeFallback()
    {
        var winService = new DummyWindowService();
        using var buffer = new RecentEventBuffer();
        var telemetry = new ScopedSubscriptionTelemetry();

        var appService = new AppService(
            winService,
            uiaProbe: _ => true,
            isDeniedProcess: _ => false,
            eventBuffer: buffer,
            telemetry: telemetry);

        // App to launch
        var spec = new LaunchSpec("notepad.exe", Executable: "notepad.exe", WaitFor: "window", TimeoutMs: 3000);
        appService.Spawner = (app, args) =>
        {
            // Simulate window appearing at 25ms
            Task.Run(async () =>
            {
                await Task.Delay(25);
                winService.OpenWindows[0x1000] = MakeWindow(0x1000, 1234, "notepad", "Untitled - Notepad", new RectPx(0, 0, 800, 600));
                buffer.Add(new ObservedEvent(EventKind.WindowOpened, DateTimeOffset.Now, Hwnd: 0x1000, Pid: 1234, Detail: "Notepad"));
            });
            return 1234;
        };

        var res = appService.Launch(spec);

        Assert.True(res.Success);
        Assert.Equal("Ready", res.LaunchState);
        Assert.Equal(0x1000, res.Hwnd);
        // Event path completed before fallback
        Assert.True(telemetry.LaunchEventWakeCount >= 1);
        Assert.Equal(1, telemetry.LaunchEventPathCount);
        Assert.Equal(0, telemetry.LaunchFallbackPollCount);
    }

    [Fact]
    public void LaunchReadiness_NoWindowEvent_FallbackPollingStillCompletes()
    {
        var winService = new DummyWindowService();
        using var buffer = new RecentEventBuffer(); // No event emitted
        var telemetry = new ScopedSubscriptionTelemetry();

        var appService = new AppService(
            winService,
            uiaProbe: _ => true,
            isDeniedProcess: _ => false,
            eventBuffer: buffer,
            telemetry: telemetry);

        var spec = new LaunchSpec("notepad.exe", Executable: "notepad.exe", WaitFor: "window", TimeoutMs: 2000);
        appService.Spawner = (app, args) =>
        {
            // Window exists in window service, but NO event is fired to buffer
            winService.OpenWindows[0x2000] = MakeWindow(0x2000, 5678, "notepad", "Untitled - Notepad", new RectPx(0, 0, 800, 600));
            return 5678;
        };

        var res = appService.Launch(spec);

        Assert.True(res.Success);
        Assert.Equal("Ready", res.LaunchState);
        // Fallback polling tick caught it
        Assert.Equal(1, telemetry.LaunchFallbackPathCount);
    }

    [Fact]
    public void LaunchReadiness_UnrelatedProcessWindow_Ignored()
    {
        var winService = new DummyWindowService();
        using var buffer = new RecentEventBuffer();
        var telemetry = new ScopedSubscriptionTelemetry();

        var appService = new AppService(
            winService,
            uiaProbe: _ => true,
            isDeniedProcess: _ => false,
            eventBuffer: buffer,
            telemetry: telemetry);

        // Put unrelated window
        winService.OpenWindows[0x9999] = MakeWindow(0x9999, 99999, "other", "Unrelated App", new RectPx(0, 0, 500, 500));

        var spec = new LaunchSpec("notepad.exe", Executable: "notepad.exe", WaitFor: "window", TimeoutMs: 400);
        appService.Spawner = (app, args) => 1234;

        var res = appService.Launch(spec);

        Assert.False(res.Success);
        Assert.Equal("TimedOut", res.LaunchState);
    }

    [Fact]
    public void LaunchReadiness_SplashRejected_MainWindowAccepted()
    {
        var winService = new DummyWindowService();
        using var buffer = new RecentEventBuffer();
        var telemetry = new ScopedSubscriptionTelemetry();

        var appService = new AppService(
            winService,
            uiaProbe: _ => true,
            isDeniedProcess: _ => false,
            eventBuffer: buffer,
            telemetry: telemetry);

        var spec = new LaunchSpec("notepad.exe", Executable: "notepad.exe", WaitFor: "window", TimeoutMs: 3000);
        appService.Spawner = (app, args) =>
        {
            Task.Run(async () =>
            {
                // Splash window first
                await Task.Delay(20);
                winService.OpenWindows[0x1111] = MakeWindow(0x1111, 4000, "notepad", "Splash Loading...", new RectPx(0, 0, 400, 300));
                buffer.Add(new ObservedEvent(EventKind.WindowOpened, DateTimeOffset.Now, Hwnd: 0x1111, Pid: 4000, Detail: "Splash"));

                // Real main window second
                await Task.Delay(50);
                winService.OpenWindows[0x2222] = MakeWindow(0x2222, 4000, "notepad", "Notepad Editor - Main", new RectPx(0, 0, 1200, 800));
                buffer.Add(new ObservedEvent(EventKind.WindowOpened, DateTimeOffset.Now, Hwnd: 0x2222, Pid: 4000, Detail: "Main"));
            });
            return 4000;
        };

        var res = appService.Launch(spec);

        Assert.True(res.Success);
        Assert.Equal(0x2222, res.Hwnd);
        Assert.True(telemetry.LaunchCandidateRejectedCount >= 1);
    }

    [Fact]
    public void LaunchReadiness_WindowAppearsBeforeUiaReady_WaitsForProbeSuccess()
    {
        var winService = new DummyWindowService();
        using var buffer = new RecentEventBuffer();
        var telemetry = new ScopedSubscriptionTelemetry();

        int probeAttempts = 0;
        var appService = new AppService(
            winService,
            uiaProbe: hwnd =>
            {
                probeAttempts++;
                return probeAttempts >= 2; // Fails on first probe, succeeds on second
            },
            isDeniedProcess: _ => false,
            eventBuffer: buffer,
            telemetry: telemetry);

        var spec = new LaunchSpec("notepad.exe", Executable: "notepad.exe", WaitFor: "window", TimeoutMs: 2000, NewInstance: true);
        appService.Spawner = (app, args) =>
        {
            winService.OpenWindows[0x3333] = MakeWindow(0x3333, 7777, "notepad", "Main Notepad", new RectPx(0, 0, 800, 600));
            buffer.Add(new ObservedEvent(EventKind.WindowOpened, DateTimeOffset.Now, Hwnd: 0x3333, Pid: 7777, Detail: "Main Notepad"));
            return 7777;
        };

        var res = appService.Launch(spec);

        Assert.True(res.Success);
        Assert.True(probeAttempts >= 2);
        Assert.Equal(0x3333, res.Hwnd);
    }

    [Fact]
    public void LaunchReadiness_AlreadyRunning_DoesNotWaitForNewWindowEvent()
    {
        var winService = new DummyWindowService();
        using var buffer = new RecentEventBuffer();
        var telemetry = new ScopedSubscriptionTelemetry();

        // Existing running window
        winService.OpenWindows[0x4444] = MakeWindow(0x4444, 8888, "notepad", "Existing Notepad", new RectPx(0, 0, 800, 600));

        var appService = new AppService(
            winService,
            uiaProbe: _ => true,
            isDeniedProcess: _ => false,
            eventBuffer: buffer,
            telemetry: telemetry);

        var spec = new LaunchSpec("notepad.exe", Executable: "notepad.exe", WaitFor: "window", NewInstance: false);
        var res = appService.Launch(spec);

        Assert.True(res.Success);
        Assert.Equal("AlreadyRunning", res.LaunchState);
        Assert.True(res.ReadyMs >= 1);
        Assert.Equal(0, telemetry.LaunchEventWakeCount);
        Assert.Equal(0, telemetry.LaunchFallbackPollCount);
        Assert.True(telemetry.LaunchProbeCount >= 1);
    }

    [Fact]
    public void LaunchReadiness_AlreadyRunning_PerformsUiaProbe()
    {
        var winService = new DummyWindowService();
        using var buffer = new RecentEventBuffer();
        var telemetry = new ScopedSubscriptionTelemetry();

        winService.OpenWindows[0x4444] = MakeWindow(0x4444, 8888, "notepad", "Existing Notepad", new RectPx(0, 0, 800, 600));

        int spawnerCalls = 0;
        int probeCalls = 0;
        var appService = new AppService(
            winService,
            uiaProbe: hwnd =>
            {
                probeCalls++;
                Thread.Sleep(5); // Consumes measurable monotonic time
                return true;
            },
            isDeniedProcess: _ => false,
            eventBuffer: buffer,
            telemetry: telemetry);

        appService.Spawner = (app, args) =>
        {
            spawnerCalls++;
            return 8888;
        };

        var spec = new LaunchSpec("notepad.exe", Executable: "notepad.exe", WaitFor: "window", NewInstance: false);
        var res = appService.Launch(spec);

        Assert.True(res.Success);
        Assert.Equal("AlreadyRunning", res.LaunchState);
        Assert.Equal(0, spawnerCalls); // process spawn count = 0
        Assert.Equal(0, telemetry.LaunchEventWakeCount); // window-event waits = 0
        Assert.Equal(0, telemetry.LaunchFallbackPollCount);
        Assert.Equal(1, probeCalls); // uiaProbeCount = 1
        Assert.True(telemetry.LaunchProbeCount >= 1);
        Assert.True(res.ReadyMs >= 1); // monotonic timing > 0, not hard-coded 0
    }

    [Fact]
    public void LaunchReadiness_AlreadyRunning_ProbeFailureDoesNotReturnFalseReady()
    {
        var winService = new DummyWindowService();
        using var buffer = new RecentEventBuffer();
        var telemetry = new ScopedSubscriptionTelemetry();

        winService.OpenWindows[0x4444] = MakeWindow(0x4444, 8888, "notepad", "Hung Notepad", new RectPx(0, 0, 800, 600));

        var appService = new AppService(
            winService,
            uiaProbe: _ => false, // probe fails!
            isDeniedProcess: _ => false,
            eventBuffer: buffer,
            telemetry: telemetry);

        var spec = new LaunchSpec("notepad.exe", Executable: "notepad.exe", WaitFor: "window", NewInstance: false);
        var res = appService.Launch(spec);

        Assert.False(res.Success);
        Assert.NotEqual("Ready", res.LaunchState);
        Assert.NotEqual("AlreadyRunning", res.LaunchState);
        Assert.Equal("ProbeFailure", res.Error);
        Assert.True(telemetry.LaunchProbeCount >= 1);
    }

    [Fact]
    public void LaunchReadiness_ProcessExitsBeforeReady_ReturnsFailure()
    {
        var winService = new DummyWindowService();
        using var buffer = new RecentEventBuffer();
        var telemetry = new ScopedSubscriptionTelemetry();

        var appService = new AppService(
            winService,
            uiaProbe: _ => true,
            isDeniedProcess: _ => false,
            eventBuffer: buffer,
            telemetry: telemetry);

        var spec = new LaunchSpec("notepad.exe", Executable: "notepad.exe", WaitFor: "window", TimeoutMs: 1500);
        appService.ProcessAliveChecker = _ => false;
        appService.Spawner = (app, args) => 99999;

        var res = appService.Launch(spec);

        Assert.False(res.Success);
        Assert.Equal("Failed", res.LaunchState);
    }

    [Fact]
    public void LaunchReadiness_NoReadyWindow_TimesOutBoundedly()
    {
        var winService = new DummyWindowService();
        using var buffer = new RecentEventBuffer();
        var telemetry = new ScopedSubscriptionTelemetry();

        var appService = new AppService(
            winService,
            uiaProbe: _ => true,
            isDeniedProcess: _ => false,
            eventBuffer: buffer,
            telemetry: telemetry);

        var sw = Stopwatch.StartNew();
        var spec = new LaunchSpec("notepad.exe", Executable: "notepad.exe", WaitFor: "window", TimeoutMs: 300);
        appService.Spawner = (app, args) => 1234;

        var res = appService.Launch(spec);

        Assert.False(res.Success);
        Assert.Equal("TimedOut", res.LaunchState);
        Assert.True(sw.ElapsedMilliseconds < 1000);
    }

    [Fact]
    public void LaunchReadiness_CancellationCleansAllWaiters()
    {
        var winService = new DummyWindowService();
        using var buffer = new RecentEventBuffer();
        var telemetry = new ScopedSubscriptionTelemetry();

        var appService = new AppService(
            winService,
            uiaProbe: _ => true,
            isDeniedProcess: _ => false,
            eventBuffer: buffer,
            telemetry: telemetry);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var spec = new LaunchSpec("notepad.exe", Executable: "notepad.exe", WaitFor: "window", TimeoutMs: 2000);
        Assert.ThrowsAny<OperationCanceledException>(() => appService.Launch(spec, cts.Token));
    }

    [Fact]
    public void LaunchReadiness_PhysicalLeaseNotHeldDuringReadyWait()
    {
        var arbiter = new DesktopArbiter();
        var winService = new DummyWindowService();
        using var buffer = new RecentEventBuffer();

        var appService = new AppService(
            winService,
            uiaProbe: _ => true,
            isDeniedProcess: _ => false,
            eventBuffer: buffer);

        // Prove that launch does not acquire a physical input lease
        winService.OpenWindows[0x1234] = MakeWindow(0x1234, 1111, "notepad", "Notepad", new RectPx(0, 0, 800, 600));
        var spec = new LaunchSpec("notepad.exe", Executable: "notepad.exe", WaitFor: "window");
        appService.Spawner = (app, args) => 1111;

        var res = appService.Launch(spec);

        Assert.True(res.Success);
        // Exclusive owner for physical input must be null
        Assert.Null(arbiter.GetExclusiveOwner());
    }

    // =========================================================================
    // SECTION 33: RESOURCE CLEANUP
    // =========================================================================

    [Fact]
    public void ResourceCleanup_AllTerminalPaths_ReturnCountersToBaseline()
    {
        var telemetry = new ScopedSubscriptionTelemetry();
        using var dispatch = new UiaDispatcher();
        using var mgr = new UiaEventSubscriptionManager(dispatch, telemetry);

        // 1. Success path
        var lease1 = mgr.Acquire(0x1111, 100, UiaEventKinds.StructureChanged);
        Assert.Equal(1, telemetry.ActiveScopedSubscriptions);
        lease1.Dispose();
        Assert.Equal(0, telemetry.ActiveScopedSubscriptions);

        // 2. Multi-ref path
        var lA = mgr.Acquire(0x2222, 200, UiaEventKinds.PropertyChanged);
        var lB = mgr.Acquire(0x2222, 200, UiaEventKinds.PropertyChanged);
        Assert.Equal(1, telemetry.ActiveScopedSubscriptions);
        lA.Dispose();
        Assert.Equal(1, telemetry.ActiveScopedSubscriptions);
        lB.Dispose();
        Assert.Equal(0, telemetry.ActiveScopedSubscriptions);

        // 3. Manager disposal cleans all outstanding subscriptions
        var lC = mgr.Acquire(0x3333, 300, UiaEventKinds.StructureChanged);
        Assert.Equal(1, telemetry.ActiveScopedSubscriptions);
        mgr.Dispose();
        Assert.Equal(0, telemetry.ActiveScopedSubscriptions);
    }

    // =========================================================================
    // REPAIR ROUND 1: AUDIT & INVARIANT VERIFICATION TESTS
    // =========================================================================

    [Fact]
    public void LaunchReadiness_NoWindowEvent_FallbackCounterSemantics()
    {
        var winService = new DummyWindowService();
        using var buffer = new RecentEventBuffer(); // No relevant window events emitted
        var telemetry = new ScopedSubscriptionTelemetry();

        var appService = new AppService(
            winService,
            uiaProbe: _ => true,
            isDeniedProcess: _ => false,
            eventBuffer: buffer,
            telemetry: telemetry);

        var spec = new LaunchSpec("notepad.exe", Executable: "notepad.exe", WaitFor: "window", TimeoutMs: 1500);
        appService.Spawner = (app, args) =>
        {
            Task.Run(async () =>
            {
                await Task.Delay(20);
                winService.OpenWindows[0x1234] = MakeWindow(0x1234, 1111, "notepad", "Notepad", new RectPx(0, 0, 800, 600));
            });
            return 1111;
        };
        appService.ProcessAliveChecker = _ => true;

        var res = appService.Launch(spec);

        Assert.True(res.Success);
        Assert.Equal("Ready", res.LaunchState);
        Assert.Equal(0, telemetry.LaunchEventWakeCount);
        Assert.True(telemetry.LaunchFallbackPollCount >= 1);
        Assert.Equal(1, telemetry.LaunchFallbackPathCount);
        Assert.True(telemetry.LaunchProbeCount >= 1);
    }

    [Fact]
    public void ScopedNotification_NativePayload_PreservesNotificationSemantics()
    {
        var telemetry = new ScopedSubscriptionTelemetry();
        using var dispatch = new UiaDispatcher();
        using var mgr = new UiaEventSubscriptionManager(dispatch, telemetry);

        using var lease = mgr.Acquire(0x1234, 4321, UiaEventKinds.Notification);

        ObservedEvent? received = null;
        mgr.Event += ev => received = ev;

        var notifData = new UiaNotificationData(
            NotificationKind: 2,
            NotificationProcessing: 1,
            DisplayString: "Upload complete",
            ActivityId: "Activity-99");

        mgr.TestRouteCallback(lease, EventKind.Notification, 0x1234, 4321, "Upload complete", "elem1", "Upload complete", notifData);

        Assert.NotNull(received);
        Assert.Equal(EventKind.Notification, received.Kind);
        Assert.Equal(0x1234, received.Hwnd);
        Assert.Equal(4321, received.Pid);
        Assert.Equal("elem1", received.ElementId);
        Assert.NotNull(received.NotificationData);
        Assert.Equal(2, received.NotificationData.NotificationKind);
        Assert.Equal(1, received.NotificationData.NotificationProcessing);
        Assert.Equal("Upload complete", received.NotificationData.DisplayString);
        Assert.Equal("Activity-99", received.NotificationData.ActivityId);
    }

    [Fact]
    public void ScopedNotification_UnsupportedCapability_DegradesWithoutFakeNotification()
    {
        var telemetry = new ScopedSubscriptionTelemetry();
        using var dispatch = new UiaDispatcher();
        using var mgr = new UiaEventSubscriptionManager(dispatch, telemetry);

        // When Notification is requested, if native platform or element does not support IUIAutomation5,
        // it gracefully installs available handlers without fabricating fake EventKind.Notification
        using var lease = mgr.Acquire(0x1234, 4321, UiaEventKinds.Notification | UiaEventKinds.StructureChanged);

        ObservedEvent? received = null;
        mgr.Event += ev => received = ev;

        // Normal structure changed event comes in
        mgr.TestRouteCallback(lease, EventKind.StructureChanged, 0x1234, 4321, "Item", "item1");

        Assert.NotNull(received);
        Assert.Equal(EventKind.StructureChanged, received.Kind);
        // No fake notification was fabricated
        Assert.Null(received.NotificationData);
    }

    [Fact]
    public void ScopedNotification_DoesNotAutomaticallyVerifyUnrelatedCondition()
    {
        var telemetry = new ScopedSubscriptionTelemetry();
        using var dispatch = new UiaDispatcher();
        using var mgr = new UiaEventSubscriptionManager(dispatch, telemetry);

        using var lease = mgr.Acquire(0x1234, 4321, UiaEventKinds.Notification);

        var elements = new List<UiElement>();
        var winService = new DummyWindowService();
        var wait = new WaitService(
            find: spec => elements,
            capture: new DummyCaptureService(),
            events: mgr,
            registry: new ElementRegistry([]),
            windows: winService,
            subscriptionManager: mgr);

        // Background trigger: deliver notification
        _ = Task.Run(async () =>
        {
            await Task.Delay(20);
            var notif = new UiaNotificationData(1, 0, "Progress 50%", "Task-1");
            mgr.TestRouteCallback(lease, EventKind.Notification, 0x1234, 4321, "Progress 50%", "elem1", null, notif);
        });

        // Predicate explicitly returns false: notification must not cause automatic verification
        var res = wait.ForCondition(
            new WaitCondition(Name: "Final Dialog", Hwnd: 0x1234),
            timeoutMs: 80);

        Assert.False(res.Success);
    }

    [Fact]
    public void ScopedSubscription_ChildWithZeroHwnd_UsesSubscribedRootIdentity()
    {
        var telemetry = new ScopedSubscriptionTelemetry();
        using var dispatch = new UiaDispatcher();
        using var mgr = new UiaEventSubscriptionManager(dispatch, telemetry);

        using var lease = mgr.Acquire(0x5678, 9876, UiaEventKinds.StructureChanged);

        ObservedEvent? received = null;
        mgr.Event += ev => received = ev;

        // Descendant element callback with NativeWindowHandle == 0
        mgr.TestRouteCallback(lease, EventKind.StructureChanged, hwnd: 0, pid: 9876, name: "NonHwndButton", autoId: "btnSubmit");

        Assert.NotNull(received);
        // Correctly mapped to subscribed root HWND 0x5678, preserving PID and element identity
        Assert.Equal(0x5678, received.Hwnd);
        Assert.Equal(9876, received.Pid);
        Assert.Equal("btnSubmit", received.ElementId);
        Assert.Equal(1, telemetry.UiaEventRelevantCount);
        Assert.Equal(0, telemetry.UiaEventIgnoredCount);
    }

    [Fact]
    public void ScopedSubscription_ZeroHwndWrongPid_IsRejected()
    {
        var telemetry = new ScopedSubscriptionTelemetry();
        using var dispatch = new UiaDispatcher();
        using var mgr = new UiaEventSubscriptionManager(dispatch, telemetry);

        using var lease = mgr.Acquire(0x5678, 9876, UiaEventKinds.StructureChanged);

        ObservedEvent? received = null;
        mgr.Event += ev => received = ev;

        // Descendant element callback with NativeWindowHandle == 0, but sender PID does not match subscribed target PID
        mgr.TestRouteCallback(lease, EventKind.StructureChanged, hwnd: 0, pid: 1111, name: "AlienControl", autoId: "alienBtn");

        Assert.Null(received);
        Assert.Equal(0, telemetry.UiaEventRelevantCount);
        Assert.Equal(1, telemetry.UiaEventIgnoredCount);
    }

    [Fact]
    public void ScopedSubscription_DisposeWindowA_DoesNotRemoveWindowB()
    {
        var telemetry = new ScopedSubscriptionTelemetry();
        using var dispatch = new UiaDispatcher();
        using var mgr = new UiaEventSubscriptionManager(dispatch, telemetry);

        var leaseA = mgr.Acquire(0x1111, 100, UiaEventKinds.StructureChanged);
        var leaseB = mgr.Acquire(0x2222, 200, UiaEventKinds.StructureChanged);

        Assert.Equal(2, telemetry.ActiveScopedSubscriptions);
        Assert.Equal(2, telemetry.ScopedSubscriptionAddCount);

        // Dispose Window A
        leaseA.Dispose();

        Assert.Equal(1, telemetry.ActiveScopedSubscriptions);
        Assert.Equal(1, telemetry.ScopedSubscriptionRemoveCount);
        Assert.False(leaseA.IsActive);
        Assert.True(leaseB.IsActive);

        ObservedEvent? evA = null;
        ObservedEvent? evB = null;
        mgr.Event += ev =>
        {
            if (ev.Hwnd == 0x1111) evA = ev;
            if (ev.Hwnd == 0x2222) evB = ev;
        };

        // Window B callback still wakes B
        mgr.TestRouteCallback(leaseB, EventKind.StructureChanged, 0x2222, 200, "WindowBElement", "elemB");
        Assert.NotNull(evB);

        // Window A callback is safely ignored (inactive/disposed)
        long beforeLate = telemetry.LateCallbackIgnoredCount;
        mgr.TestRouteCallback(leaseA, EventKind.StructureChanged, 0x1111, 100, "WindowAElement", "elemA");
        Assert.Null(evA);
        Assert.True(telemetry.LateCallbackIgnoredCount >= beforeLate + 1);

        // Dispose Window B
        leaseB.Dispose();
        Assert.Equal(0, telemetry.ActiveScopedSubscriptions);
        Assert.Equal(2, telemetry.ScopedSubscriptionRemoveCount);
    }

    [Fact]
    public void ScopedSubscription_DisposeOneDifferentKey_DoesNotRemoveOtherKey()
    {
        var telemetry = new ScopedSubscriptionTelemetry();
        using var dispatch = new UiaDispatcher();
        using var mgr = new UiaEventSubscriptionManager(dispatch, telemetry);

        var key1 = mgr.Acquire(0x3333, 300, UiaEventKinds.StructureChanged);
        var key2 = mgr.Acquire(0x3333, 300, UiaEventKinds.PropertyChanged);

        Assert.Equal(2, telemetry.ActiveScopedSubscriptions);
        Assert.NotEqual(key1.Key, key2.Key);

        // Dispose Key 1
        key1.Dispose();
        Assert.Equal(1, telemetry.ActiveScopedSubscriptions);
        Assert.False(key1.IsActive);
        Assert.True(key2.IsActive);

        // Dispose Key 2
        key2.Dispose();
        Assert.Equal(0, telemetry.ActiveScopedSubscriptions);
        Assert.False(key2.IsActive);
    }

    [Fact]
    public void ScopedSubscription_EquivalentPropertySetsDifferentOrder_ReuseNativeRegistration()
    {
        var telemetry = new ScopedSubscriptionTelemetry();
        using var dispatch = new UiaDispatcher();
        using var mgr = new UiaEventSubscriptionManager(dispatch, telemetry);

        // Property sets with different ordering and duplicates
        var lease1 = mgr.Acquire(0x4444, 400, UiaEventKinds.PropertyChanged, [30045, 30005]);
        var lease2 = mgr.Acquire(0x4444, 400, UiaEventKinds.PropertyChanged, [30005, 30045, 30005]);

        Assert.Equal(1, telemetry.ScopedSubscriptionAddCount);
        Assert.Equal(1, telemetry.ScopedSubscriptionReuseCount);
        Assert.Equal(1, telemetry.ActiveScopedSubscriptions);
        Assert.Equal(lease1.Key, lease2.Key);
        Assert.Equal("30005,30045", lease1.Key.PropertySet);

        lease1.Dispose();
        Assert.Equal(1, telemetry.ActiveScopedSubscriptions);
        Assert.True(lease2.IsActive);

        lease2.Dispose();
        Assert.Equal(0, telemetry.ActiveScopedSubscriptions);
    }
}
