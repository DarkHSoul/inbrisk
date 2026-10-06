using System.Collections.Concurrent;
using System.Diagnostics;
using Inbrisk.Core;
using Inbrisk.Platform.Windows.Uia;
using Inbrisk.Runtime;
using Interop.UIAutomationClient;
using Xunit;

namespace Inbrisk.Tests;

/// <summary>
/// Phase H Round 2: UIA Read Concurrency & Serial Mutation Lane validation tests.
/// Verifies bounded MTA parallel reading, apartment preservation, cancellation, deterministic aggregation,
/// authoritative FIFO mutation serialization, DesktopArbiter integration, and starvation-free fairness.
/// </summary>
public sealed class PhaseHRound2ConcurrencyTests
{
    [Fact]
    public async Task ReadPool_IndependentReadsExecuteConcurrently()
    {
        // Arrange: Pool with 4 MTA worker threads and a synchronization barrier
        using var pool = new UiaReadPool(maxConcurrency: 4);
        using var barrier = new Barrier(4);

        // Act: Enqueue 4 independent reads that synchronize at the barrier
        var tasks = Enumerable.Range(0, 4).Select(i => pool.RunAsync(uia =>
        {
            var reached = barrier.SignalAndWait(3000);
            return (Index: i, Reached: reached);
        })).ToArray();

        var results = await Task.WhenAll(tasks);

        // Assert: All 4 readers ran concurrently and reached the barrier simultaneously
        Assert.Equal(4, results.Length);
        Assert.All(results, r => Assert.True(r.Reached, "Reader should reach barrier in parallel with peers"));
        Assert.True(pool.PeakConcurrency >= 2, $"Expected peak concurrency >= 2, but was {pool.PeakConcurrency}");
    }

    [Fact]
    public async Task ReadPool_MaxConcurrencyIsBounded()
    {
        // Arrange: Pool strictly bounded to 2 concurrent MTA readers
        using var pool = new UiaReadPool(maxConcurrency: 2);
        var activeReaders = 0;
        var maxObservedActive = 0;

        // Act: Enqueue 6 concurrent reads
        var tasks = Enumerable.Range(0, 6).Select(i => pool.RunAsync(uia =>
        {
            var current = Interlocked.Increment(ref activeReaders);
            int currentMax;
            do
            {
                currentMax = Volatile.Read(ref maxObservedActive);
                if (current <= currentMax) break;
            } while (Interlocked.CompareExchange(ref maxObservedActive, current, currentMax) != currentMax);

            Thread.Sleep(40);
            Interlocked.Decrement(ref activeReaders);
            return i;
        })).ToArray();

        var results = await Task.WhenAll(tasks);

        // Assert: At no point did active concurrency exceed 2
        Assert.Equal(6, results.Length);
        Assert.True(maxObservedActive <= 2, $"Max observed active readers ({maxObservedActive}) exceeded bound 2");
        Assert.True(pool.PeakConcurrency <= 2, $"Peak concurrency ({pool.PeakConcurrency}) exceeded bound 2");
        Assert.Equal(6, pool.CompletedCount);
    }

    [Fact]
    public async Task ReadPool_UiaObjectsStayOnOwningApartment()
    {
        // Arrange: Pool with 2 dedicated MTA workers
        using var pool = new UiaReadPool(maxConcurrency: 2);

        // Act: Dispatch reads to capture thread apartments and UIA instances
        var tasks = Enumerable.Range(0, 6).Select(i => pool.RunAsync(uia =>
        {
            var threadId = Thread.CurrentThread.ManagedThreadId;
            var aptState = Thread.CurrentThread.GetApartmentState();
            return (ThreadId: threadId, ApartmentState: aptState, Uia: uia);
        })).ToArray();

        var results = await Task.WhenAll(tasks);

        // Assert:
        // 1. All worker threads must run in MTA apartment
        Assert.All(results, r => Assert.Equal(ApartmentState.MTA, r.ApartmentState));

        // 2. All executions occurred on dedicated pool worker threads
        var knownWorkerIds = pool.WorkerThreadIds;
        Assert.All(results, r => Assert.Contains(r.ThreadId, knownWorkerIds));

        // 3. UIA context is pinned to its owning thread: executions on the same thread see the same instance,
        // and instances never cross between different worker threads.
        var groups = results.GroupBy(r => r.ThreadId).ToList();
        foreach (var group in groups)
        {
            var expectedUia = group.First().Uia;
            foreach (var item in group)
            {
                Assert.Same(expectedUia, item.Uia);
            }
        }

        if (groups.Count > 1 && groups[0].First().Uia != null)
        {
            Assert.NotSame(groups[0].First().Uia, groups[1].First().Uia);
        }
    }

    [Fact]
    public async Task ReadPool_CancelQueuedReadSkipsWork()
    {
        // Arrange: Concurrency = 1. Worker is busy with blocking read.
        using var pool = new UiaReadPool(maxConcurrency: 1);
        using var workerBlocker = new ManualResetEventSlim(false);
        using var workerStarted = new ManualResetEventSlim(false);
        using var secondItemCts = new CancellationTokenSource();

        var secondWorkExecuted = false;

        // Item 1 occupies the only worker thread
        var task1 = pool.RunAsync(uia =>
        {
            workerStarted.Set();
            workerBlocker.Wait(3000);
            return 1;
        });

        Assert.True(workerStarted.Wait(2000), "Worker should have started item 1");

        // Act: Enqueue item 2 with cancellable token, then cancel it while in queue
        var task2 = pool.RunAsync(uia =>
        {
            secondWorkExecuted = true;
            return 2;
        }, ct: secondItemCts.Token);

        // Cancel while still waiting in queue
        secondItemCts.Cancel();

        // Release worker 1 so worker can consume next queue item
        workerBlocker.Set();
        await task1;

        // Assert: task2 throws OperationCanceledException and delegate was NEVER executed
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task2);
        Assert.False(secondWorkExecuted, "Work delegate must NOT be executed for cancelled queued read");
        Assert.True(pool.CancelledCount >= 1, "Cancelled count should be incremented");
    }

    [Fact]
    public async Task ReadPool_ParallelResultsRemainDeterministic()
    {
        // Arrange: 4 parallel workers, 10 inputs with intentionally inverted sleep times
        using var pool = new UiaReadPool(maxConcurrency: 4);
        var inputs = Enumerable.Range(0, 10).ToList();

        // Act: GatherAsync across inputs. Index 9 finishes first (10ms), Index 0 finishes last (100ms)
        var results = await pool.GatherAsync(inputs, (uia, item) =>
        {
            Thread.Sleep((10 - item) * 10);
            return $"item-{item}";
        });

        // Assert: Even though execution finished out-of-order, output strictly preserves input sequence
        var expected = inputs.Select(i => $"item-{i}").ToList();
        Assert.Equal(expected, results);
    }

    [Fact]
    public async Task MutationLane_MutationsNeverOverlap()
    {
        // Arrange: Single authoritative mutation lane
        using var lane = new SerialMutationLane();
        var activeMutations = 0;
        var overlapDetected = 0;

        // Act: Submit 8 mutations concurrently
        var tasks = Enumerable.Range(0, 8).Select(i => lane.ExecuteAsync(() =>
        {
            var current = Interlocked.Increment(ref activeMutations);
            if (current > 1)
            {
                Interlocked.Increment(ref overlapDetected);
            }

            Thread.Sleep(20);
            Interlocked.Decrement(ref activeMutations);
            return i;
        })).ToArray();

        var results = await Task.WhenAll(tasks);

        // Assert: Zero overlaps, max concurrency = 1
        Assert.Equal(8, results.Length);
        Assert.Equal(0, overlapDetected);
        Assert.Equal(1, lane.PeakConcurrency);
        Assert.Equal(8, lane.CompletedCount);
    }

    [Fact]
    public async Task MutationLane_OrderIsDeterministic()
    {
        // Arrange: Single authoritative mutation lane
        using var lane = new SerialMutationLane();
        var executionLog = new ConcurrentQueue<int>();

        // Act: Enqueue 10 mutations sequentially
        var tasks = new List<Task<int>>();
        for (var i = 0; i < 10; i++)
        {
            var index = i;
            tasks.Add(lane.ExecuteAsync(() =>
            {
                executionLog.Enqueue(index);
                Thread.Sleep(5);
                return index;
            }));
        }

        await Task.WhenAll(tasks);

        // Assert: Execution strictly followed FIFO submission sequence
        var expected = Enumerable.Range(0, 10).ToArray();
        Assert.Equal(expected, executionLog.ToArray());
    }

    [Fact]
    public async Task MutationLane_SharedAcrossLogicalSessions()
    {
        // Arrange: Shared DesktopArbiter and mutation lane shared between two logical sessions
        using var arbiter = new DesktopArbiter();
        using var lane = new SerialMutationLane(arbiter: arbiter);

        var activeOwners = new ConcurrentBag<string>();
        var overlaps = 0;
        var currentActive = 0;

        // Act: Concurrently dispatch mutations from session-A and session-B
        var taskA = lane.ExecuteAsync("session-A", () =>
        {
            var cur = Interlocked.Increment(ref currentActive);
            if (cur > 1) Interlocked.Increment(ref overlaps);

            var owner = arbiter.GetExclusiveOwner();
            if (owner != null) activeOwners.Add(owner.OwnerId);

            Thread.Sleep(30);
            Interlocked.Decrement(ref currentActive);
            return "A_Done";
        }, description: "Mutation from Session A");

        var taskB = lane.ExecuteAsync("session-B", () =>
        {
            var cur = Interlocked.Increment(ref currentActive);
            if (cur > 1) Interlocked.Increment(ref overlaps);

            var owner = arbiter.GetExclusiveOwner();
            if (owner != null) activeOwners.Add(owner.OwnerId);

            Thread.Sleep(30);
            Interlocked.Decrement(ref currentActive);
            return "B_Done";
        }, description: "Mutation from Session B");

        var results = await Task.WhenAll(taskA, taskB);

        // Assert:
        // 1. Both sessions succeeded without overlapping
        Assert.Contains("A_Done", results);
        Assert.Contains("B_Done", results);
        Assert.Equal(0, overlaps);
        Assert.Equal(1, lane.PeakConcurrency);

        // 2. Both sessions held their respective exclusive leases on DesktopArbiter
        Assert.Contains("session-A", activeOwners);
        Assert.Contains("session-B", activeOwners);
    }

    [Fact]
    public async Task ReadTraffic_DoesNotStarveMutation()
    {
        // Arrange: Read pool and mutation lane linked together
        using var pool = new UiaReadPool(maxConcurrency: 2);
        using var lane = new SerialMutationLane(readPool: pool);

        using var stopReadsCts = new CancellationTokenSource();
        var readsCompleted = 0;

        // Act 1: Flood read pool with continuous read traffic
        var readStreamTask = Task.Run(async () =>
        {
            while (!stopReadsCts.IsCancellationRequested)
            {
                try
                {
                    await pool.RunAsync(uia =>
                    {
                        Thread.Sleep(5);
                        Interlocked.Increment(ref readsCompleted);
                        return true;
                    }, ct: stopReadsCts.Token);
                }
                catch (OperationCanceledException) { break; }
            }
        });

        // Wait until read traffic is actively flowing
        var waitSw = Stopwatch.StartNew();
        while (Volatile.Read(ref readsCompleted) < 5 && waitSw.ElapsedMilliseconds < 2000)
        {
            await Task.Delay(10);
        }

        // Act 2: Submit a mutation into the active read stream
        var mutationSw = Stopwatch.StartNew();
        var mutationExecuted = false;

        await lane.ExecuteAsync(() =>
        {
            mutationExecuted = true;
            Thread.Sleep(15);
        }, timeout: TimeSpan.FromSeconds(3));

        mutationSw.Stop();
        stopReadsCts.Cancel();
        await readStreamTask;

        // Assert: Mutation executed promptly without being starved by endless incoming reads
        Assert.True(mutationExecuted, "Mutation must execute");
        Assert.True(mutationSw.ElapsedMilliseconds < 1500,
            $"Mutation was starved by read traffic! Took {mutationSw.ElapsedMilliseconds}ms");
        Assert.True(readsCompleted >= 5, "Reads must have been active");
    }

    [Fact]
    public async Task MutationTraffic_DoesNotPermanentlyStarveReads()
    {
        // Arrange: Linked pool and lane with yield threshold = 3 consecutive mutations
        using var pool = new UiaReadPool(maxConcurrency: 2);
        using var lane = new SerialMutationLane(readPool: pool, maxConsecutiveMutationsBeforeYield: 3);

        var completedMutations = 0;
        var completedMutationsAtReadFinish = -1;

        // Act 1: Enqueue a barrage of 20 mutations (would take ~400ms without yields)
        var mutationTasks = Enumerable.Range(0, 20).Select(i => lane.ExecuteAsync(() =>
        {
            Thread.Sleep(15);
            Interlocked.Increment(ref completedMutations);
            return i;
        })).ToArray();

        // Wait until 2 mutations have completed so mutation traffic is actively saturated
        var waitSw = Stopwatch.StartNew();
        while (Volatile.Read(ref completedMutations) < 2 && waitSw.ElapsedMilliseconds < 2000)
        {
            await Task.Delay(5);
        }

        // Act 2: Submit a read amidst the heavy mutation traffic
        var readTask = pool.RunAsync(uia =>
        {
            completedMutationsAtReadFinish = Volatile.Read(ref completedMutations);
            return 999;
        }, timeoutMs: 5000);

        var readResult = await readTask;
        await Task.WhenAll(mutationTasks);

        // Assert:
        // Read completed successfully and did NOT have to wait until all 20 mutations finished.
        // It was granted a fair execution window by the lane's yield mechanism!
        Assert.Equal(999, readResult);
        Assert.True(completedMutationsAtReadFinish < 20,
            $"Read was permanently starved until all mutations completed! Finished at mutation {completedMutationsAtReadFinish}/20");
    }
}
