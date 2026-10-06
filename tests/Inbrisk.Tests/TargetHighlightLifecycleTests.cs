using Inbrisk.Platform.Windows.Indicator;
using Xunit;

namespace Inbrisk.Tests;

public sealed class TargetHighlightLifecycleTests
{
    [Fact]
    public void Token_BeginActivity_HighlightsWindow()
    {
        using var service = new TargetHighlightService(enableWatchdog: false);
        long hwnd = 0x1234;

        Assert.False(service.IsWindowHighlighted(hwnd));

        using (service.BeginWindowActivity(hwnd, "test-owner"))
        {
            Assert.True(service.IsWindowHighlighted(hwnd));
            Assert.Equal(1, service.ActiveTokenCount(hwnd));
            Assert.Contains(hwnd, service.GetHighlightedWindows());
        }

        Assert.False(service.IsWindowHighlighted(hwnd));
        Assert.Equal(0, service.ActiveTokenCount(hwnd));
        Assert.DoesNotContain(hwnd, service.GetHighlightedWindows());
    }

    [Fact]
    public void Token_Dispose_ClearsHighlight()
    {
        using var service = new TargetHighlightService(enableWatchdog: false);
        long hwnd = 0x5678;

        var lease = service.BeginWindowActivity(hwnd, "test-owner");
        Assert.True(service.IsWindowHighlighted(hwnd));

        lease.Dispose();
        Assert.False(service.IsWindowHighlighted(hwnd));

        // Multiple disposes are idempotent
        lease.Dispose();
        Assert.False(service.IsWindowHighlighted(hwnd));
    }

    [Fact]
    public void MultipleTokens_SameWindow_RefCounted()
    {
        using var service = new TargetHighlightService(enableWatchdog: false);
        long hwnd = 0xAAAA;

        var lease1 = service.BeginWindowActivity(hwnd, "session-1");
        var lease2 = service.BeginWindowActivity(hwnd, "session-2");

        Assert.True(service.IsWindowHighlighted(hwnd));
        Assert.Equal(2, service.ActiveTokenCount(hwnd));

        lease1.Dispose();
        Assert.True(service.IsWindowHighlighted(hwnd), "Highlight must remain while lease2 is still active");
        Assert.Equal(1, service.ActiveTokenCount(hwnd));

        lease2.Dispose();
        Assert.False(service.IsWindowHighlighted(hwnd));
        Assert.Equal(0, service.ActiveTokenCount(hwnd));
    }

    [Fact]
    public void Highlight_ClearsOnOperationSuccess()
    {
        using var service = new TargetHighlightService(enableWatchdog: false);
        long hwnd = 0x1000;

        IDisposable? lease = null;
        try
        {
            lease = service.BeginWindowActivity(hwnd, "owner");
            Assert.True(service.IsWindowHighlighted(hwnd));
            // Simulate successful operation
        }
        finally
        {
            lease?.Dispose();
        }

        Assert.False(service.IsWindowHighlighted(hwnd));
    }

    [Fact]
    public void Highlight_ClearsOnOperationFailure()
    {
        using var service = new TargetHighlightService(enableWatchdog: false);
        long hwnd = 0x1001;

        IDisposable? lease = null;
        try
        {
            lease = service.BeginWindowActivity(hwnd, "owner");
            Assert.True(service.IsWindowHighlighted(hwnd));
            // Simulate failed operation (early exit)
        }
        finally
        {
            lease?.Dispose();
        }

        Assert.False(service.IsWindowHighlighted(hwnd));
    }

    [Fact]
    public void Highlight_ClearsOnCancellation()
    {
        using var service = new TargetHighlightService(enableWatchdog: false);
        long hwnd = 0x1002;

        var cts = new CancellationTokenSource();
        IDisposable? lease = null;
        try
        {
            lease = service.BeginWindowActivity(hwnd, "owner");
            cts.Cancel();
            cts.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
        finally
        {
            lease?.Dispose();
        }

        Assert.False(service.IsWindowHighlighted(hwnd));
    }

    [Fact]
    public void Highlight_ClearsOnTimeout()
    {
        using var service = new TargetHighlightService(enableWatchdog: false);
        long hwnd = 0x1003;

        IDisposable? lease = null;
        try
        {
            lease = service.BeginWindowActivity(hwnd, "owner");
            throw new TimeoutException("Simulated timeout");
        }
        catch (TimeoutException)
        {
            // Expected
        }
        finally
        {
            lease?.Dispose();
        }

        Assert.False(service.IsWindowHighlighted(hwnd));
    }

    [Fact]
    public void Highlight_ClearsOnException()
    {
        using var service = new TargetHighlightService(enableWatchdog: false);
        long hwnd = 0x1004;

        IDisposable? lease = null;
        try
        {
            lease = service.BeginWindowActivity(hwnd, "owner");
            throw new InvalidOperationException("Unexpected error during execution");
        }
        catch (InvalidOperationException)
        {
            // Expected
        }
        finally
        {
            lease?.Dispose();
        }

        Assert.False(service.IsWindowHighlighted(hwnd));
    }

    [Fact]
    public void Highlight_ClearsOnSessionDisconnect()
    {
        using var service = new TargetHighlightService(enableWatchdog: false);
        long winA = 0x2001;
        long winB = 0x2002;

        service.BeginWindowActivity(winA, "session-A");
        service.BeginWindowActivity(winB, "session-B");

        Assert.True(service.IsWindowHighlighted(winA));
        Assert.True(service.IsWindowHighlighted(winB));

        // Session A disconnects
        service.OnSessionDisconnected("session-A");

        Assert.False(service.IsWindowHighlighted(winA), "Session A windows must clear highlight on disconnect");
        Assert.True(service.IsWindowHighlighted(winB), "Session B windows must remain unaffected");
    }

    [Fact]
    public void Highlight_ClearsOnEmergencyStop()
    {
        using var service = new TargetHighlightService(enableWatchdog: false);
        long hwnd = 0x3001;

        service.BeginWindowActivity(hwnd, "session");
        Assert.True(service.IsWindowHighlighted(hwnd));

        // Emergency panic triggered
        service.SetEmergency(true);

        Assert.False(service.IsWindowHighlighted(hwnd), "Emergency stop must instantly suppress all highlights");
        Assert.Empty(service.GetHighlightedWindows());

        // New attempts while stopped return empty
        using (service.BeginWindowActivity(hwnd, "session"))
        {
            Assert.False(service.IsWindowHighlighted(hwnd));
        }

        // Resume clears emergency lock
        service.SetEmergency(false);
        Assert.False(service.IsWindowHighlighted(hwnd));
    }

    [Fact]
    public void Highlight_WatchdogFailsafe_ClearsAbandoned()
    {
        // 50ms watchdog timeout for test speed
        using var service = new TargetHighlightService(watchdogTimeoutMs: 50, enableWatchdog: true);
        long hwnd = 0x4001;

        service.BeginWindowActivity(hwnd, "abandoned-owner");
        Assert.True(service.IsWindowHighlighted(hwnd));

        // Wait for watchdog to scan and clean up stale abandoned token (>50ms + scan tick)
        bool cleared = SpinWait.SpinUntil(() => !service.IsWindowHighlighted(hwnd), 2500);
        Assert.True(cleared, "Watchdog must reap abandoned highlight tokens after inactivity cutoff");
    }

    [Fact]
    public void CompositeRun_MaintainsStableHighlight_OnSameTarget()
    {
        using var service = new TargetHighlightService(enableWatchdog: false);
        long targetHwnd = 0x5001;

        IDisposable? planLease = null;
        try
        {
            // Plan begins targeting targetHwnd
            planLease = service.BeginWindowActivity(targetHwnd, "plan-session");
            Assert.True(service.IsWindowHighlighted(targetHwnd));

            // Step 1 on targetHwnd
            Assert.True(service.IsWindowHighlighted(targetHwnd));

            // Step 2 on targetHwnd
            Assert.True(service.IsWindowHighlighted(targetHwnd));

            // Step 3 on targetHwnd
            Assert.True(service.IsWindowHighlighted(targetHwnd));
        }
        finally
        {
            planLease?.Dispose();
        }

        // When plan completes, highlight is released
        Assert.False(service.IsWindowHighlighted(targetHwnd));
    }

    [Fact]
    public void CompositeRun_TransfersHighlight_OnTargetChange()
    {
        using var service = new TargetHighlightService(enableWatchdog: false);
        long hwnd1 = 0x6001;
        long hwnd2 = 0x6002;

        IDisposable? currentLease = null;
        try
        {
            // Step 1: Target window 1
            currentLease = service.BeginWindowActivity(hwnd1, "plan");
            Assert.True(service.IsWindowHighlighted(hwnd1));
            Assert.False(service.IsWindowHighlighted(hwnd2));

            // Step 2: Target switches to window 2
            currentLease.Dispose();
            currentLease = service.BeginWindowActivity(hwnd2, "plan");

            Assert.False(service.IsWindowHighlighted(hwnd1), "Previous window highlight must clear on target switch");
            Assert.True(service.IsWindowHighlighted(hwnd2), "New target window must acquire highlight");
        }
        finally
        {
            currentLease?.Dispose();
        }

        Assert.False(service.IsWindowHighlighted(hwnd1));
        Assert.False(service.IsWindowHighlighted(hwnd2));
    }
}
