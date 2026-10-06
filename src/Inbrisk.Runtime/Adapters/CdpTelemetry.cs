using System.Text.Json.Serialization;

namespace Inbrisk.Runtime.Adapters;

/// <summary>
/// Operational and diagnostic telemetry for persistent Chrome DevTools Protocol (CDP) sessions.
/// Tracks connection lifecycle, command correlation, event pipelines, and DOM wait performance.
/// </summary>
public sealed class CdpTelemetry
{
    private long _sessionCount;
    private long _connectionsOpened;
    private long _connectionsClosed;
    private long _connectionReuses;
    private long _connectionFailures;
    private long _reconnectAttempts;
    private long _reconnectSuccesses;

    private long _targetDiscoveryCalls;

    private long _commandsSent;
    private long _responsesReceived;
    private long _pendingCommands;
    private long _unknownResponseIds;
    private long _lateEpochMessagesIgnored;

    private long _eventsReceived;
    private long _eventsDropped;

    private long _pageLoadEventCount;
    private long _lifecycleEventCount;
    private long _networkIdleCount;
    private long _readinessFallbackPollCount;

    private long _domWaitCount;
    private long _domWaitImmediateHitCount;
    private long _domWaitMutationHitCount;
    private long _domWaitTimeoutCount;
    private long _domWaitCancellationCount;

    private long _networkEventsBuffered;
    private long _consoleEventsBuffered;
    private long _bufferDroppedCount;

    private long _activeReceiveLoops;
    private long _activePageDomWaits;
    private long _pageObserverInstallCount;
    private long _pageObserverDisconnectCount;
    private long _pageObserverCancellationCleanupCount;
    private long _pageObserverCleanupAcknowledgedCount;
    private long _pageObserverCleanupDegradedCount;

    public long ActiveSessions => Interlocked.Read(ref _sessionCount);
    public long ActiveReceiveLoops => Interlocked.Read(ref _activeReceiveLoops);
    public long ActivePageDomWaits => Interlocked.Read(ref _activePageDomWaits);
    public long PageObserverInstallCount => Interlocked.Read(ref _pageObserverInstallCount);
    public long PageObserverDisconnectCount => Interlocked.Read(ref _pageObserverDisconnectCount);
    public long PageObserverCancellationCleanupCount => Interlocked.Read(ref _pageObserverCancellationCleanupCount);
    public long PageObserverCleanupAcknowledgedCount => Interlocked.Read(ref _pageObserverCleanupAcknowledgedCount);
    public long PageObserverCleanupDegradedCount => Interlocked.Read(ref _pageObserverCleanupDegradedCount);

    public long CdpSessionCount => Interlocked.Read(ref _sessionCount);
    public long CdpConnectionsOpened => Interlocked.Read(ref _connectionsOpened);
    public long ConnectionsOpened => Interlocked.Read(ref _connectionsOpened);
    public long CdpConnectionsClosed => Interlocked.Read(ref _connectionsClosed);
    public long ConnectionsClosed => Interlocked.Read(ref _connectionsClosed);
    public long CdpConnectionReuses => Interlocked.Read(ref _connectionReuses);
    public long ConnectionReuses => Interlocked.Read(ref _connectionReuses);
    public long CdpConnectionFailures => Interlocked.Read(ref _connectionFailures);
    public long CdpReconnectAttempts => Interlocked.Read(ref _reconnectAttempts);
    public long CdpReconnectSuccesses => Interlocked.Read(ref _reconnectSuccesses);

    public long CdpTargetDiscoveryCalls => Interlocked.Read(ref _targetDiscoveryCalls);

    public long CdpCommandsSent => Interlocked.Read(ref _commandsSent);
    public long CdpResponsesReceived => Interlocked.Read(ref _responsesReceived);
    public long CdpPendingCommands => Interlocked.Read(ref _pendingCommands);
    public long CdpUnknownResponseIds => Interlocked.Read(ref _unknownResponseIds);
    public long CdpLateEpochMessagesIgnored => Interlocked.Read(ref _lateEpochMessagesIgnored);

    public long CdpEventsReceived => Interlocked.Read(ref _eventsReceived);
    public long CdpEventsDropped => Interlocked.Read(ref _eventsDropped);

    public long CdpPageLoadEventCount => Interlocked.Read(ref _pageLoadEventCount);
    public long CdpLifecycleEventCount => Interlocked.Read(ref _lifecycleEventCount);
    public long CdpNetworkIdleCount => Interlocked.Read(ref _networkIdleCount);
    public long CdpReadinessFallbackPollCount => Interlocked.Read(ref _readinessFallbackPollCount);

    public long CdpDomWaitCount => Interlocked.Read(ref _domWaitCount);
    public long CdpDomWaitImmediateHitCount => Interlocked.Read(ref _domWaitImmediateHitCount);
    public long CdpDomWaitMutationHitCount => Interlocked.Read(ref _domWaitMutationHitCount);
    public long CdpDomWaitTimeoutCount => Interlocked.Read(ref _domWaitTimeoutCount);
    public long CdpDomWaitCancellationCount => Interlocked.Read(ref _domWaitCancellationCount);

    public long CdpNetworkEventsBuffered => Interlocked.Read(ref _networkEventsBuffered);
    public long CdpConsoleEventsBuffered => Interlocked.Read(ref _consoleEventsBuffered);
    public long CdpBufferDroppedCount => Interlocked.Read(ref _bufferDroppedCount);

    public void IncSessionCount() => Interlocked.Increment(ref _sessionCount);
    public void DecSessionCount() => Interlocked.Decrement(ref _sessionCount);
    public void IncConnectionsOpened() => Interlocked.Increment(ref _connectionsOpened);
    public void IncConnectionsClosed() => Interlocked.Increment(ref _connectionsClosed);
    public void IncConnectionReuses() => Interlocked.Increment(ref _connectionReuses);
    public void IncConnectionFailures() => Interlocked.Increment(ref _connectionFailures);
    public void IncReconnectAttempts() => Interlocked.Increment(ref _reconnectAttempts);
    public void IncReconnectSuccesses() => Interlocked.Increment(ref _reconnectSuccesses);

    public void IncTargetDiscoveryCalls() => Interlocked.Increment(ref _targetDiscoveryCalls);

    public void IncCommandsSent() => Interlocked.Increment(ref _commandsSent);
    public void IncResponsesReceived() => Interlocked.Increment(ref _responsesReceived);
    public void IncPendingCommands() => Interlocked.Increment(ref _pendingCommands);
    public void DecPendingCommands() => Interlocked.Decrement(ref _pendingCommands);
    public void IncUnknownResponseIds() => Interlocked.Increment(ref _unknownResponseIds);
    public void IncLateEpochMessagesIgnored() => Interlocked.Increment(ref _lateEpochMessagesIgnored);

    public void IncEventsReceived() => Interlocked.Increment(ref _eventsReceived);
    public void IncEventsDropped() => Interlocked.Increment(ref _eventsDropped);

    public void IncPageLoadEventCount() => Interlocked.Increment(ref _pageLoadEventCount);
    public void IncLifecycleEventCount() => Interlocked.Increment(ref _lifecycleEventCount);
    public void IncNetworkIdleCount() => Interlocked.Increment(ref _networkIdleCount);
    public void IncReadinessFallbackPollCount() => Interlocked.Increment(ref _readinessFallbackPollCount);

    public void IncDomWaitCount() => Interlocked.Increment(ref _domWaitCount);
    public void IncDomWaitImmediateHit() => Interlocked.Increment(ref _domWaitImmediateHitCount);
    public void IncDomWaitMutationHit() => Interlocked.Increment(ref _domWaitMutationHitCount);
    public void IncDomWaitTimeout() => Interlocked.Increment(ref _domWaitTimeoutCount);
    public void IncDomWaitCancellation() => Interlocked.Increment(ref _domWaitCancellationCount);

    public void IncActiveReceiveLoops() => Interlocked.Increment(ref _activeReceiveLoops);
    public void DecActiveReceiveLoops() => Interlocked.Decrement(ref _activeReceiveLoops);
    public void IncActivePageDomWaits() => Interlocked.Increment(ref _activePageDomWaits);
    public void DecActivePageDomWaits() => Interlocked.Decrement(ref _activePageDomWaits);
    public void IncPageObserverInstall() => Interlocked.Increment(ref _pageObserverInstallCount);
    public void IncPageObserverDisconnect() => Interlocked.Increment(ref _pageObserverDisconnectCount);
    public void IncPageObserverCancellationCleanup() => Interlocked.Increment(ref _pageObserverCancellationCleanupCount);
    public void IncPageObserverCleanupAcknowledged() => Interlocked.Increment(ref _pageObserverCleanupAcknowledgedCount);
    public void IncPageObserverCleanupDegraded() => Interlocked.Increment(ref _pageObserverCleanupDegradedCount);

    public void IncNetworkEventsBuffered() => Interlocked.Increment(ref _networkEventsBuffered);
    public void IncConsoleEventsBuffered() => Interlocked.Increment(ref _consoleEventsBuffered);
    public void IncBufferDroppedCount() => Interlocked.Increment(ref _bufferDroppedCount);

    public void Reset()
    {
        Interlocked.Exchange(ref _activeReceiveLoops, 0);
        Interlocked.Exchange(ref _activePageDomWaits, 0);
        Interlocked.Exchange(ref _pageObserverInstallCount, 0);
        Interlocked.Exchange(ref _pageObserverDisconnectCount, 0);
        Interlocked.Exchange(ref _pageObserverCancellationCleanupCount, 0);
        Interlocked.Exchange(ref _pageObserverCleanupAcknowledgedCount, 0);
        Interlocked.Exchange(ref _pageObserverCleanupDegradedCount, 0);
        Interlocked.Exchange(ref _sessionCount, 0);
        Interlocked.Exchange(ref _connectionsOpened, 0);
        Interlocked.Exchange(ref _connectionsClosed, 0);
        Interlocked.Exchange(ref _connectionReuses, 0);
        Interlocked.Exchange(ref _connectionFailures, 0);
        Interlocked.Exchange(ref _reconnectAttempts, 0);
        Interlocked.Exchange(ref _reconnectSuccesses, 0);
        Interlocked.Exchange(ref _targetDiscoveryCalls, 0);
        Interlocked.Exchange(ref _commandsSent, 0);
        Interlocked.Exchange(ref _responsesReceived, 0);
        Interlocked.Exchange(ref _pendingCommands, 0);
        Interlocked.Exchange(ref _unknownResponseIds, 0);
        Interlocked.Exchange(ref _lateEpochMessagesIgnored, 0);
        Interlocked.Exchange(ref _eventsReceived, 0);
        Interlocked.Exchange(ref _eventsDropped, 0);
        Interlocked.Exchange(ref _pageLoadEventCount, 0);
        Interlocked.Exchange(ref _lifecycleEventCount, 0);
        Interlocked.Exchange(ref _networkIdleCount, 0);
        Interlocked.Exchange(ref _readinessFallbackPollCount, 0);
        Interlocked.Exchange(ref _domWaitCount, 0);
        Interlocked.Exchange(ref _domWaitImmediateHitCount, 0);
        Interlocked.Exchange(ref _domWaitMutationHitCount, 0);
        Interlocked.Exchange(ref _domWaitTimeoutCount, 0);
        Interlocked.Exchange(ref _domWaitCancellationCount, 0);
        Interlocked.Exchange(ref _networkEventsBuffered, 0);
        Interlocked.Exchange(ref _consoleEventsBuffered, 0);
        Interlocked.Exchange(ref _bufferDroppedCount, 0);
    }
}
