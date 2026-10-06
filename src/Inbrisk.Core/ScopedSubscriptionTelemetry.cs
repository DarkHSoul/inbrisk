namespace Inbrisk.Core;

public sealed class ScopedSubscriptionTelemetry
{
    private long _scopedSubscriptionAddCount;
    private long _scopedSubscriptionReuseCount;
    private long _scopedSubscriptionRemoveCount;
    private long _activeScopedSubscriptions;
    private long _desktopRootSubscriptionCount;
    private long _uiaEventCallbackCount;
    private long _uiaEventRelevantCount;
    private long _uiaEventIgnoredCount;
    private long _uiaEventCoalescedCount;
    private long _lateCallbackIgnoredCount;
    private long _callbackExtraComReadCount;

    private long _launchEventWakeCount;
    private long _launchFallbackPollCount;
    private long _launchCandidateCount;
    private long _launchCandidateRejectedCount;
    private long _launchProbeCount;
    private long _launchReadyMs;
    private long _launchTimeToFirstWindowMs;
    private long _launchEventPathCount;
    private long _launchFallbackPathCount;

    public long ScopedSubscriptionAddCount => Interlocked.Read(ref _scopedSubscriptionAddCount);
    public long ScopedSubscriptionReuseCount => Interlocked.Read(ref _scopedSubscriptionReuseCount);
    public long ScopedSubscriptionRemoveCount => Interlocked.Read(ref _scopedSubscriptionRemoveCount);
    public long ActiveScopedSubscriptions => Interlocked.Read(ref _activeScopedSubscriptions);
    public long DesktopRootSubscriptionCount => Interlocked.Read(ref _desktopRootSubscriptionCount);
    public long UiaEventCallbackCount => Interlocked.Read(ref _uiaEventCallbackCount);
    public long UiaEventRelevantCount => Interlocked.Read(ref _uiaEventRelevantCount);
    public long UiaEventIgnoredCount => Interlocked.Read(ref _uiaEventIgnoredCount);
    public long UiaEventCoalescedCount => Interlocked.Read(ref _uiaEventCoalescedCount);
    public long LateCallbackIgnoredCount => Interlocked.Read(ref _lateCallbackIgnoredCount);
    public long CallbackExtraComReadCount => Interlocked.Read(ref _callbackExtraComReadCount);

    public long LaunchEventWakeCount => Interlocked.Read(ref _launchEventWakeCount);
    public long LaunchFallbackPollCount => Interlocked.Read(ref _launchFallbackPollCount);
    public long LaunchCandidateCount => Interlocked.Read(ref _launchCandidateCount);
    public long LaunchCandidateRejectedCount => Interlocked.Read(ref _launchCandidateRejectedCount);
    public long LaunchProbeCount => Interlocked.Read(ref _launchProbeCount);
    public long LaunchReadyMs => Interlocked.Read(ref _launchReadyMs);
    public long LaunchTimeToFirstWindowMs => Interlocked.Read(ref _launchTimeToFirstWindowMs);
    public long LaunchEventPathCount => Interlocked.Read(ref _launchEventPathCount);
    public long LaunchFallbackPathCount => Interlocked.Read(ref _launchFallbackPathCount);

    public void IncScopedSubscriptionAdd() => Interlocked.Increment(ref _scopedSubscriptionAddCount);
    public void IncScopedSubscriptionReuse() => Interlocked.Increment(ref _scopedSubscriptionReuseCount);
    public void IncScopedSubscriptionRemove() => Interlocked.Increment(ref _scopedSubscriptionRemoveCount);
    public void IncActiveScopedSubscriptions() => Interlocked.Increment(ref _activeScopedSubscriptions);
    public void DecActiveScopedSubscriptions() => Interlocked.Decrement(ref _activeScopedSubscriptions);
    public void IncDesktopRootSubscription() => Interlocked.Increment(ref _desktopRootSubscriptionCount);
    public void IncUiaEventCallback() => Interlocked.Increment(ref _uiaEventCallbackCount);
    public void IncUiaEventRelevant() => Interlocked.Increment(ref _uiaEventRelevantCount);
    public void IncUiaEventIgnored() => Interlocked.Increment(ref _uiaEventIgnoredCount);
    public void IncUiaEventCoalesced() => Interlocked.Increment(ref _uiaEventCoalescedCount);
    public void IncLateCallbackIgnored() => Interlocked.Increment(ref _lateCallbackIgnoredCount);
    public void IncCallbackExtraComRead() => Interlocked.Increment(ref _callbackExtraComReadCount);

    public void IncLaunchEventWake() => Interlocked.Increment(ref _launchEventWakeCount);
    public void IncLaunchFallbackPoll() => Interlocked.Increment(ref _launchFallbackPollCount);
    public void IncLaunchCandidate() => Interlocked.Increment(ref _launchCandidateCount);
    public void IncLaunchCandidateRejected() => Interlocked.Increment(ref _launchCandidateRejectedCount);
    public void IncLaunchProbe() => Interlocked.Increment(ref _launchProbeCount);
    public void SetLaunchReadyMs(long ms) => Interlocked.Exchange(ref _launchReadyMs, ms);
    public void SetLaunchTimeToFirstWindowMs(long ms) => Interlocked.Exchange(ref _launchTimeToFirstWindowMs, ms);
    public void IncLaunchEventPath() => Interlocked.Increment(ref _launchEventPathCount);
    public void IncLaunchFallbackPath() => Interlocked.Increment(ref _launchFallbackPathCount);

    public void Reset()
    {
        Interlocked.Exchange(ref _scopedSubscriptionAddCount, 0);
        Interlocked.Exchange(ref _scopedSubscriptionReuseCount, 0);
        Interlocked.Exchange(ref _scopedSubscriptionRemoveCount, 0);
        Interlocked.Exchange(ref _activeScopedSubscriptions, 0);
        Interlocked.Exchange(ref _desktopRootSubscriptionCount, 0);
        Interlocked.Exchange(ref _uiaEventCallbackCount, 0);
        Interlocked.Exchange(ref _uiaEventRelevantCount, 0);
        Interlocked.Exchange(ref _uiaEventIgnoredCount, 0);
        Interlocked.Exchange(ref _uiaEventCoalescedCount, 0);
        Interlocked.Exchange(ref _lateCallbackIgnoredCount, 0);
        Interlocked.Exchange(ref _callbackExtraComReadCount, 0);

        Interlocked.Exchange(ref _launchEventWakeCount, 0);
        Interlocked.Exchange(ref _launchFallbackPollCount, 0);
        Interlocked.Exchange(ref _launchCandidateCount, 0);
        Interlocked.Exchange(ref _launchCandidateRejectedCount, 0);
        Interlocked.Exchange(ref _launchProbeCount, 0);
        Interlocked.Exchange(ref _launchReadyMs, 0);
        Interlocked.Exchange(ref _launchTimeToFirstWindowMs, 0);
        Interlocked.Exchange(ref _launchEventPathCount, 0);
        Interlocked.Exchange(ref _launchFallbackPathCount, 0);
    }
}
