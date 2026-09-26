using System.Diagnostics;
using Inbrisk.Platform.Windows.Uia;
using Xunit;

namespace Inbrisk.Tests;

public sealed class UiaQueueCancellationTests
{
    [Fact]
    public void Dispatcher_Run_WithPreCancelledToken_ThrowsImmediately()
    {
        using var dispatcher = new UiaDispatcher();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var executed = false;
        var sw = Stopwatch.StartNew();

        Assert.Throws<OperationCanceledException>(() =>
        {
            dispatcher.Run(uia =>
            {
                executed = true;
                return 42;
            }, timeoutMs: 5000, ct: cts.Token, intentName: "PreCancelledTest");
        });

        Assert.False(executed);
        Assert.True(sw.ElapsedMilliseconds < 500, $"expected instant return, took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task Dispatcher_Cancellation_DropsQueuedWork_WithoutExecutingCOM()
    {
        using var dispatcher = new UiaDispatcher();
        using var firstBlocking = new ManualResetEventSlim(false);
        using var firstStarted = new ManualResetEventSlim(false);
        using var secondCts = new CancellationTokenSource();

        var firstExecuted = false;
        var secondExecuted = false;

        // 1. Enqueue first work item that blocks the dispatcher loop
        var task1 = Task.Run(() =>
        {
            return dispatcher.Run(uia =>
            {
                firstExecuted = true;
                firstStarted.Set();
                firstBlocking.Wait(5000);
                return 1;
            }, timeoutMs: 10000, intentName: "BlockingWork");
        });

        Assert.True(firstStarted.Wait(3000), "first work item never started on dispatcher thread");

        // 2. Enqueue second work item behind it with a cancellable token
        var task2 = Task.Run(() =>
        {
            return dispatcher.Run(uia =>
            {
                secondExecuted = true;
                return 2;
            }, timeoutMs: 5000, ct: secondCts.Token, intentName: "CancellableWork");
        });

        // Small pause to ensure task2 is placed in _queue
        await Task.Delay(50);

        // 3. Cancel second work item while it is still waiting in queue
        secondCts.Cancel();

        // 4. Release first work item so dispatcher thread continues
        firstBlocking.Set();
        await task1;

        // 5. task2 must complete as cancelled, and secondExecuted must remain false
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task2);

        Assert.True(firstExecuted);
        Assert.False(secondExecuted, "second work item must NOT have executed after cancellation");
    }

    [Fact]
    public async Task Dispatcher_PurgeQueue_IncrementsGeneration_AndCancelsQueuedWork()
    {
        using var dispatcher = new UiaDispatcher();
        using var firstBlocking = new ManualResetEventSlim(false);
        using var firstStarted = new ManualResetEventSlim(false);

        var executedItems = new List<int>();

        // 1. First item blocks the loop
        var t0 = Task.Run(() =>
        {
            return dispatcher.Run(uia =>
            {
                firstStarted.Set();
                firstBlocking.Wait(5000);
                return 0;
            }, timeoutMs: 10000, intentName: "Blocker");
        });

        Assert.True(firstStarted.Wait(3000));

        // 2. Enqueue 3 items while dispatcher is busy
        var tasks = new List<Task<int>>();
        for (var i = 1; i <= 3; i++)
        {
            var idx = i;
            tasks.Add(Task.Run(() => dispatcher.Run(uia =>
            {
                lock (executedItems) executedItems.Add(idx);
                return idx;
            }, timeoutMs: 5000, intentName: $"Item-{idx}")));
        }

        await Task.Delay(50);
        var genBefore = dispatcher.CurrentGeneration;

        // 3. Purge queue
        dispatcher.PurgeQueue();
        var genAfter = dispatcher.CurrentGeneration;
        Assert.True(genAfter > genBefore, "PurgeQueue must increment generation");

        // 4. Release blocker
        firstBlocking.Set();
        await t0;

        // 5. All queued items must have been cancelled
        foreach (var task in tasks)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
        }

        Assert.Empty(executedItems);
        Assert.Equal(0, dispatcher.QueuedCount);
    }
}
