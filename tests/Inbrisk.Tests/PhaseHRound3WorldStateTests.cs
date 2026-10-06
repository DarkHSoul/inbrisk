using Inbrisk.Core;
using Inbrisk.Core.WorldState;
using Xunit;

namespace Inbrisk.Tests;

/// <summary>
/// Phase H Round 3 (H3) Tests: Versioned World State (stateVersion + expect + guard + delta).
/// Verifies:
/// 1. Monotonic stateVersion progression for relevant physical/semantic world changes.
/// 2. Strict isolation ensuring telemetry counters and read-only queries do not bump stateVersion.
/// 3. Expect matching allows mutation; stale expect blocks mutation with compact conflict error.
/// 4. TOCTOU HARD RULE: Re-verification inside the serialized mutation boundary prevents race conditions.
/// 5. Invariant guards (exists, notExists, focused) pass when satisfied and block mutation when violated.
/// 6. Incremental delta querying through bounded ring buffer and compact resync snapshot fallback.
/// 7. Bounded event storm coalescing preventing infinite coalescing and 1:1 explosion.
/// 8. Seamless composition of expect + guards with composite batch execution.
/// </summary>
public sealed class PhaseHRound3WorldStateTests
{
    // =========================================================================
    // 1. STATE VERSION MONOTONICITY & TELEMETRY ISOLATION
    // =========================================================================

    [Fact]
    public void StateVersion_MonotonicForRelevantChanges()
    {
        var tracker = new StateVersionTracker(initialVersion: 100);
        var baseTime = DateTimeOffset.UtcNow;

        var v0 = tracker.CurrentVersion;
        Assert.Equal(100, v0);

        // Relevant Change 1: Window Opened
        var evtWindowOpen = new ObservedEvent(
            EventKind.WindowOpened,
            baseTime,
            Hwnd: 0x1001,
            Detail: "Calculator opened");
        var v1 = tracker.RecordEvent(evtWindowOpen, baseTime);
        Assert.True(v1 > v0, "Version must increase monotonically on WindowOpened.");

        // Relevant Change 2: Foreground Window Changed (after coalesce window)
        var t2 = baseTime.AddMilliseconds(100);
        var evtForeground = new ObservedEvent(
            EventKind.ForegroundChanged,
            t2,
            Hwnd: 0x1001,
            Detail: "Calculator focused");
        var v2 = tracker.RecordEvent(evtForeground, t2);
        Assert.True(v2 > v1, "Version must increase monotonically on ForegroundChanged.");

        // Relevant Change 3: Window Closed
        var t3 = t2.AddMilliseconds(100);
        var evtWindowClosed = new ObservedEvent(
            EventKind.WindowClosed,
            t3,
            Hwnd: 0x1001,
            Detail: "Calculator closed");
        var v3 = tracker.RecordEvent(evtWindowClosed, t3);
        Assert.True(v3 > v2, "Version must increase monotonically on WindowClosed.");

        // Relevant Change 4: Explicit Mutation Side Effect
        var t4 = t3.AddMilliseconds(100);
        var v4 = tracker.RecordMutation("click", "Clicked Submit button", hwnd: 0x2000, elementId: "btn_ok", timestamp: t4);
        Assert.True(v4 > v3, "Version must increase monotonically on explicit mutation.");

        Assert.Equal(104, tracker.CurrentVersion);
    }

    [Fact]
    public void StateVersion_DoesNotIncrementForTelemetryOnlyChanges()
    {
        var tracker = new StateVersionTracker(initialVersion: 50);

        // Diagnostic Telemetry calls
        tracker.RecordTelemetry("metrics.frame_rate", 60.0);
        tracker.RecordTelemetry("metrics.uia_latency_ms", 14.2, new Dictionary<string, object?> { ["adapter"] = "uia" });
        Assert.Equal(50, tracker.CurrentVersion);

        // Read-only queries
        tracker.RecordReadOnlyQuery("computer_inspect", "0x1234");
        tracker.RecordReadOnlyQuery("computer_find", "name:Submit");
        Assert.Equal(50, tracker.CurrentVersion);

        // Passive Notification events (non-state mutating)
        var notifEvent = new ObservedEvent(
            EventKind.Notification,
            DateTimeOffset.UtcNow,
            Detail: "Background diagnostic log");
        var vAfterNotif = tracker.RecordEvent(notifEvent);

        Assert.Equal(50, vAfterNotif);
        Assert.Equal(50, tracker.CurrentVersion);
    }

    // =========================================================================
    // 2. EXPECTATION VALIDATION & TOCTOU BOUNDARY PROTECTION
    // =========================================================================

    [Fact]
    public void Expect_MatchingVersion_AllowsMutation()
    {
        var tracker = new StateVersionTracker(initialVersion: 42);
        var stateReader = new InMemoryWorldStateReader(versionGetter: () => tracker.CurrentVersion);
        var validator = new ExpectGuardValidator(stateReader, tracker);

        var expect = StateExpectation.Version(42);
        bool mutationExecuted = false;

        var result = validator.ExecuteInMutationLane(
            expect: expect,
            guards: null,
            mutationAction: () =>
            {
                mutationExecuted = true;
                return "executed_ok";
            },
            mutationKind: "test_action");

        Assert.True(result.Success);
        Assert.True(mutationExecuted);
        Assert.Equal("executed_ok", result.Result);
        Assert.Null(result.ErrorCode);
        Assert.Equal(43, tracker.CurrentVersion);
        Assert.Equal(43, result.StateVersion);
    }

    [Fact]
    public void Expect_StaleVersion_BlocksMutation()
    {
        var tracker = new StateVersionTracker(initialVersion: 42);
        var stateReader = new InMemoryWorldStateReader(versionGetter: () => tracker.CurrentVersion);
        var validator = new ExpectGuardValidator(stateReader, tracker);

        // Stale expectation: caller expected version 41, but live state is 42
        var staleExpect = StateExpectation.Version(41);
        bool mutationExecuted = false;

        var preflight = validator.ValidatePreflight(staleExpect);
        Assert.False(preflight.IsAllowed);
        Assert.Equal("Conflict", preflight.ErrorCode);

        var result = validator.ExecuteInMutationLane(
            expect: staleExpect,
            guards: null,
            mutationAction: () =>
            {
                mutationExecuted = true;
                return "should_not_run";
            });

        Assert.False(result.Success);
        Assert.False(mutationExecuted);
        Assert.Equal("Conflict", result.ErrorCode);
        Assert.Contains("expected version 41", result.ErrorMessage);
        Assert.Contains("live version is 42", result.ErrorMessage);
        Assert.Equal(42, tracker.CurrentVersion);
    }

    [Fact]
    public void Expect_RecheckedInsideMutationLane()
    {
        // TOCTOU HARD RULE: Preflight check passes outside the lock, but before entering
        // the serialized mutation boundary, a concurrent change bumps stateVersion.
        // The check inside the mutation lane MUST re-verify and block the race condition.
        var tracker = new StateVersionTracker(initialVersion: 10);
        var stateReader = new InMemoryWorldStateReader(versionGetter: () => tracker.CurrentVersion);
        var validator = new ExpectGuardValidator(stateReader, tracker);

        var expect = StateExpectation.Version(10);

        // Step 1: Preflight outside the mutation boundary passes
        var preflight = validator.ValidatePreflight(expect);
        Assert.True(preflight.IsAllowed);

        // Step 2: Concurrent world change occurs before lane execution
        tracker.RecordMutation("concurrent_popup", "System dialog appeared");
        Assert.Equal(11, tracker.CurrentVersion);

        // Step 3: Enter mutation lane with stale expect=10
        bool mutationExecuted = false;
        var result = validator.ExecuteInMutationLane(
            expect: expect,
            guards: null,
            mutationAction: () =>
            {
                mutationExecuted = true;
                return "racy_mutation";
            });

        Assert.False(result.Success);
        Assert.False(mutationExecuted);
        Assert.Equal("Conflict", result.ErrorCode);
        Assert.Contains("expected version 10", result.ErrorMessage);
        Assert.Contains("live version is 11", result.ErrorMessage);
    }

    // =========================================================================
    // 3. INVARIANT GUARDS: EXISTS, NOT_EXISTS, FOCUSED
    // =========================================================================

    [Fact]
    public void Guard_Exists_Passes()
    {
        var stateReader = new InMemoryWorldStateReader(initialVersion: 1);
        stateReader.Windows.Add(0x2001);
        stateReader.Elements.Add("btn_submit");
        stateReader.SelectorMatcher = spec => spec.Name == "Submit";

        var validator = new ExpectGuardValidator(stateReader);

        var guards = new[]
        {
            StateGuard.ExistsWindow(0x2001),
            StateGuard.Exists("btn_submit"),
            StateGuard.Exists(new FindSpec(Name: "Submit"))
        };

        bool mutationRan = false;
        var result = validator.ExecuteInMutationLane(
            expect: null,
            guards: guards,
            mutationAction: () =>
            {
                mutationRan = true;
                return 123;
            });

        Assert.True(result.Success);
        Assert.True(mutationRan);
        Assert.Equal(123, result.Result);
    }

    [Fact]
    public void Guard_Exists_FailureBlocksMutation()
    {
        var stateReader = new InMemoryWorldStateReader(initialVersion: 1);
        stateReader.Elements.Add("btn_cancel"); // "btn_submit" is missing

        var validator = new ExpectGuardValidator(stateReader);
        var missingGuard = StateGuard.Exists("btn_submit");

        bool mutationRan = false;
        var result = validator.ExecuteInMutationLane(
            expect: null,
            guards: new[] { missingGuard },
            mutationAction: () =>
            {
                mutationRan = true;
                return "should_not_execute";
            });

        Assert.False(result.Success);
        Assert.False(mutationRan);
        Assert.Equal("GuardFailed", result.ErrorCode);
        Assert.Equal(missingGuard, result.FailedGuard);
        Assert.Contains("Element 'btn_submit' does not exist", result.ErrorMessage);
    }

    [Fact]
    public void Guard_Focused_BlocksUnexpectedTarget()
    {
        var stateReader = new InMemoryWorldStateReader(initialVersion: 1)
        {
            FocusedHwnd = 0x1111 // Foreground is window 0x1111
        };

        var validator = new ExpectGuardValidator(stateReader);
        var focusGuard = StateGuard.FocusedWindow(0x2222); // Expected 0x2222

        bool mutationRan = false;
        var result = validator.ExecuteInMutationLane(
            expect: null,
            guards: new[] { focusGuard },
            mutationAction: () =>
            {
                mutationRan = true;
                return 999;
            });

        Assert.False(result.Success);
        Assert.False(mutationRan);
        Assert.Equal("GuardFailed", result.ErrorCode);
        Assert.Equal(focusGuard, result.FailedGuard);
        Assert.Contains("not the focused foreground window", result.ErrorMessage);
    }

    [Fact]
    public void Guard_FailureZeroSideEffect_ThenCurrentVersionSuccess()
    {
        // Chain verification:
        // 1. Initial attempt with failing guard -> rejection with GuardFailed, ZERO side-effects, version unchanged.
        // 2. State updated to satisfy guard -> subsequent execution succeeds with current stateVersion.
        var tracker = new StateVersionTracker(initialVersion: 50);
        var stateReader = new InMemoryWorldStateReader(versionGetter: () => tracker.CurrentVersion)
        {
            FocusedHwnd = 0x1111 // Currently focused window is 0x1111
        };
        var validator = new ExpectGuardValidator(stateReader, tracker);

        var guard = StateGuard.FocusedWindow(0x2222); // Requires 0x2222 to be focused
        bool mutationExecuted1 = false;

        // Step 1: Execute with failing guard
        var failResult = validator.ExecuteInMutationLane(
            expect: StateExpectation.Version(50),
            guards: new[] { guard },
            mutationAction: () =>
            {
                mutationExecuted1 = true;
                return "failed_attempt";
            },
            mutationKind: "step1");

        // Assert: Failure with zero side-effects
        Assert.False(failResult.Success);
        Assert.False(mutationExecuted1, "Mutation action must NOT execute when guard fails");
        Assert.Equal("GuardFailed", failResult.ErrorCode);
        Assert.Equal(guard, failResult.FailedGuard);
        Assert.Equal(50, tracker.CurrentVersion); // Zero state mutation / version unchanged

        // Step 2: Satisfy the guard condition (e.g. window 0x2222 gains focus)
        stateReader.FocusedHwnd = 0x2222;
        bool mutationExecuted2 = false;

        // Step 3: Subsequent execution with satisfied guard and current stateVersion
        var successResult = validator.ExecuteInMutationLane(
            expect: StateExpectation.Version(50),
            guards: new[] { guard },
            mutationAction: () =>
            {
                mutationExecuted2 = true;
                return "success_result";
            },
            mutationKind: "step2");

        // Assert: Succeeded, mutation executed, stateVersion incremented
        Assert.True(successResult.Success);
        Assert.True(mutationExecuted2, "Mutation must execute when guard is satisfied");
        Assert.Equal("success_result", successResult.Result);
        Assert.Equal(51, tracker.CurrentVersion);
        Assert.Equal(51, successResult.StateVersion);
    }

    // =========================================================================
    // 4. BOUNDED STATE DELTA RING BUFFER & RESYNC FALLBACK
    // =========================================================================

    [Fact]
    public void StateDelta_ReturnsChangesSinceKnownVersion()
    {
        var ring = new StateDeltaRing(capacity: 10);
        var tracker = new StateVersionTracker(initialVersion: 1, deltaRing: ring);

        // Record 3 sequential mutations (versions 2, 3, 4)
        tracker.RecordMutation("click", "Clicked step 1");
        tracker.RecordMutation("type", "Typed text");
        tracker.RecordMutation("hotkey", "Dispatched Enter");

        Assert.Equal(4, tracker.CurrentVersion);

        // Query delta since version 1
        var delta = ring.GetChangesSince(knownVersion: 1);

        Assert.False(delta.RequiresResync);
        Assert.Equal(4, delta.CurrentVersion);
        Assert.Equal(1, delta.KnownVersion);
        Assert.Equal(3, delta.Changes.Count);

        Assert.Equal(2, delta.Changes[0].Version);
        Assert.Equal("Mutation:click", delta.Changes[0].ChangeKind);

        Assert.Equal(3, delta.Changes[1].Version);
        Assert.Equal("Mutation:type", delta.Changes[1].ChangeKind);

        Assert.Equal(4, delta.Changes[2].Version);
        Assert.Equal("Mutation:hotkey", delta.Changes[2].ChangeKind);
    }

    [Fact]
    public void StateDelta_OldVersionRequiresResync()
    {
        // Ring buffer with small capacity of 3 items
        var ring = new StateDeltaRing(capacity: 3);
        var tracker = new StateVersionTracker(initialVersion: 1, deltaRing: ring);

        // Record 6 changes (versions 2, 3, 4, 5, 6, 7)
        // With capacity 3, versions 2, 3, 4 have been evicted from the ring
        for (int i = 0; i < 6; i++)
        {
            tracker.RecordMutation($"step_{i}", $"Action {i}");
        }

        Assert.Equal(7, tracker.CurrentVersion);
        Assert.Equal(3, ring.Count);
        Assert.Equal(5, ring.OldestRetainedVersion);

        // Querying for evicted version 2 must return RequiresResync = true
        var snapshotCalled = false;
        var delta = ring.GetChangesSince(knownVersion: 2, snapshotProvider: () =>
        {
            snapshotCalled = true;
            return new CompactWorldSnapshot(
                Version: 7,
                Timestamp: DateTimeOffset.UtcNow,
                Windows: new[] { new CompactWindowSnapshot(0x3001, 1234, "MainApp", true) },
                Elements: new[] { new CompactElementSnapshot("btn_ok", "Button", "OK", new RectPx(0, 0, 50, 20)) });
        });

        Assert.True(delta.RequiresResync);
        Assert.True(snapshotCalled);
        Assert.NotNull(delta.CompactSnapshot);
        Assert.Equal(7, delta.CompactSnapshot.Version);
        Assert.Single(delta.CompactSnapshot.Windows);
        Assert.Empty(delta.Changes);
    }

    // =========================================================================
    // 5. BOUNDED EVENT STORM COALESCING
    // =========================================================================

    [Fact]
    public void StateVersion_EventStormCoalescingIsBounded()
    {
        // Coalescing configuration:
        // Quiet window = 50ms, MaxCoalescedCount = 5, MaxBurstDuration = 200ms
        var options = new StateVersionTrackerOptions(
            CoalesceWindow: TimeSpan.FromMilliseconds(50),
            MaxCoalesceDuration: TimeSpan.FromMilliseconds(200),
            MaxCoalescedCount: 5);

        var tracker = new StateVersionTracker(initialVersion: 1, options: options);
        var baseTime = DateTimeOffset.UtcNow;

        // Simulate 20 rapid UI events spaced 2ms apart (rapid event storm)
        for (int i = 0; i < 20; i++)
        {
            var eventTime = baseTime.AddMilliseconds(i * 2);
            var evt = new ObservedEvent(
                EventKind.LocationChanged,
                eventTime,
                Hwnd: 0x1000,
                Detail: $"Moving cursor {i}");

            tracker.RecordEvent(evt, eventTime);
        }

        // Invariant Verification:
        // 1. Coalescing prevented 1:1 explosion (20 increments would be 21).
        // 2. Coalescing was BOUNDED by MaxCoalescedCount = 5:
        //    Events 0..4 = batch 1 (ver 2)
        //    Events 5..9 = batch 2 (ver 3)
        //    Events 10..14 = batch 3 (ver 4)
        //    Events 15..19 = batch 4 (ver 5)
        // Expected final version is 5.
        Assert.Equal(5, tracker.CurrentVersion);
    }

    // =========================================================================
    // 6. COMPOSITE BATCH EXECUTION WITH EXPECT & GUARDS
    // =========================================================================

    [Fact]
    public void Batch_ExpectAndGuardComposeCorrectly()
    {
        var tracker = new StateVersionTracker(initialVersion: 10);
        var stateReader = new InMemoryWorldStateReader(versionGetter: () => tracker.CurrentVersion);
        stateReader.Windows.Add(0x4001);
        stateReader.Elements.Add("txt_user");
        stateReader.FocusedHwnd = 0x4001;

        var validator = new ExpectGuardValidator(stateReader, tracker);

        // Case A: Valid Expect + Valid Guards -> All steps execute
        var validGuards = new[]
        {
            StateGuard.ExistsWindow(0x4001),
            StateGuard.Exists("txt_user"),
            StateGuard.FocusedWindow(0x4001)
        };

        var stepResults = new List<string>();
        var batchSteps = new Func<object?>[]
        {
            () => { stepResults.Add("focus_txt"); return "focused"; },
            () => { stepResults.Add("type_alice"); return "typed"; },
            () => { stepResults.Add("press_tab"); return "tabbed"; }
        };

        var successBatch = validator.ExecuteBatchInMutationLane(
            expect: StateExpectation.Version(10),
            guards: validGuards,
            steps: batchSteps,
            batchDescription: "fill_user_form");

        Assert.True(successBatch.Success);
        Assert.Equal(3, successBatch.StepsExecuted);
        Assert.Equal(3, stepResults.Count);
        Assert.Equal(11, tracker.CurrentVersion);

        // Case B: Stale Expect -> Batch blocked immediately before running any steps
        stepResults.Clear();
        var staleBatch = validator.ExecuteBatchInMutationLane(
            expect: StateExpectation.Version(10), // live version is now 11
            guards: validGuards,
            steps: batchSteps);

        Assert.False(staleBatch.Success);
        Assert.Equal(0, staleBatch.StepsExecuted);
        Assert.Empty(stepResults);
        Assert.Equal("Conflict", staleBatch.ErrorCode);

        // Case C: Failing Guard -> Batch blocked immediately before running any steps
        stepResults.Clear();
        var failingGuards = new[]
        {
            StateGuard.Exists("non_existent_element")
        };

        var guardFailedBatch = validator.ExecuteBatchInMutationLane(
            expect: StateExpectation.Version(11),
            guards: failingGuards,
            steps: batchSteps);

        Assert.False(guardFailedBatch.Success);
        Assert.Equal(0, guardFailedBatch.StepsExecuted);
        Assert.Empty(stepResults);
        Assert.Equal("GuardFailed", guardFailedBatch.ErrorCode);
        Assert.NotNull(guardFailedBatch.FailedGuard);
        Assert.Equal("non_existent_element", guardFailedBatch.FailedGuard.ElementId);
    }
}
