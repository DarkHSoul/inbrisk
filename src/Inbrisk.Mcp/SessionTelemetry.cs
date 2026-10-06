using System;
using System.Collections.Generic;
using System.Threading;
using Inbrisk.Core;

namespace Inbrisk.Mcp;

/// <summary>
/// Session-scoped counters for Section C redundant tool call patterns.
/// Aggregates metrics without adding bloat to MCP tool responses.
/// Emitted to PerfTrace / debug logs.
/// </summary>
public sealed class SessionTelemetry
{
    private long _duplicateReadSuppressed;
    private long _duplicateMutationRetrySuppressed;
    private long _postVerifiedObservation;
    private long _launchFollowupDiscovery;
    private long _closeRetry;
    private long _appsThenLaunchWithin5s;
    private long _sequentialSingleActionRunLength;
    private long _observedChangeFollowup;

    public long DuplicateReadSuppressed => Interlocked.Read(ref _duplicateReadSuppressed);
    public long DuplicateMutationRetrySuppressed => Interlocked.Read(ref _duplicateMutationRetrySuppressed);
    public long PostVerifiedObservation => Interlocked.Read(ref _postVerifiedObservation);
    public long LaunchFollowupDiscovery => Interlocked.Read(ref _launchFollowupDiscovery);
    public long CloseRetry => Interlocked.Read(ref _closeRetry);
    public long AppsThenLaunchWithin5s => Interlocked.Read(ref _appsThenLaunchWithin5s);
    public long LaunchPrecededByApps => AppsThenLaunchWithin5s; // Backwards-compatible alias
    public long SequentialSingleActionRunLength => Interlocked.Read(ref _sequentialSingleActionRunLength);
    public long ObservedChangeFollowup => Interlocked.Read(ref _observedChangeFollowup);

    public void IncDuplicateReadSuppressed() => Interlocked.Increment(ref _duplicateReadSuppressed);
    public void IncDuplicateMutationRetrySuppressed() => Interlocked.Increment(ref _duplicateMutationRetrySuppressed);
    public void IncPostVerifiedObservation() => Interlocked.Increment(ref _postVerifiedObservation);
    public void IncLaunchFollowupDiscovery() => Interlocked.Increment(ref _launchFollowupDiscovery);
    public void IncCloseRetry() => Interlocked.Increment(ref _closeRetry);
    public void IncAppsThenLaunchWithin5s() => Interlocked.Increment(ref _appsThenLaunchWithin5s);
    public void IncLaunchPrecededByApps() => IncAppsThenLaunchWithin5s(); // Backwards-compatible alias
    public void IncSequentialSingleAction() => Interlocked.Increment(ref _sequentialSingleActionRunLength);
    public void ResetSequentialSingleAction() => Interlocked.Exchange(ref _sequentialSingleActionRunLength, 0);
    public void IncObservedChangeFollowup() => Interlocked.Increment(ref _observedChangeFollowup);

    private long _initialMapBuildCount;
    private long _initialMapBuildTotalMs;
    private long _initialMapNodesVisitedTotal;
    private long _initialMapComReadsTotal;
    private long _initialMapActionablesTotal;
    private long _initialMapLandmarksTotal;
    private long _initialMapPayloadBytesTotal;
    private long _initialMapTruncatedCount;
    private long _lastInitialMapBuildMs;
    private long _lastInitialMapNodesVisited;
    private long _lastInitialMapPayloadBytes;
    private long _lastInitialMapTruncated;

    public long InitialMapBuildCount => Interlocked.Read(ref _initialMapBuildCount);
    public long InitialMapBuildTotalMs => Interlocked.Read(ref _initialMapBuildTotalMs);
    public long InitialMapNodesVisitedTotal => Interlocked.Read(ref _initialMapNodesVisitedTotal);
    public long InitialMapComReadsTotal => Interlocked.Read(ref _initialMapComReadsTotal);
    public long InitialMapActionablesTotal => Interlocked.Read(ref _initialMapActionablesTotal);
    public long InitialMapLandmarksTotal => Interlocked.Read(ref _initialMapLandmarksTotal);
    public long InitialMapPayloadBytesTotal => Interlocked.Read(ref _initialMapPayloadBytesTotal);
    public long InitialMapTruncatedCount => Interlocked.Read(ref _initialMapTruncatedCount);
    public long LastInitialMapBuildMs => Interlocked.Read(ref _lastInitialMapBuildMs);
    public long LastInitialMapNodesVisited => Interlocked.Read(ref _lastInitialMapNodesVisited);
    public long LastInitialMapPayloadBytes => Interlocked.Read(ref _lastInitialMapPayloadBytes);
    public bool LastInitialMapTruncated => Interlocked.Read(ref _lastInitialMapTruncated) == 1;

    public void RecordInitialMapMetrics(double buildMs, int nodesVisited, int actionables, int landmarks, int payloadBytes, bool truncated, int comReads = 0)
    {
        Interlocked.Increment(ref _initialMapBuildCount);
        Interlocked.Add(ref _initialMapBuildTotalMs, (long)Math.Round(buildMs));
        Interlocked.Add(ref _initialMapNodesVisitedTotal, nodesVisited);
        Interlocked.Add(ref _initialMapComReadsTotal, comReads);
        Interlocked.Add(ref _initialMapActionablesTotal, actionables);
        Interlocked.Add(ref _initialMapLandmarksTotal, landmarks);
        Interlocked.Add(ref _initialMapPayloadBytesTotal, payloadBytes);
        if (truncated) Interlocked.Increment(ref _initialMapTruncatedCount);

        Interlocked.Exchange(ref _lastInitialMapBuildMs, (long)Math.Round(buildMs));
        Interlocked.Exchange(ref _lastInitialMapNodesVisited, nodesVisited);
        Interlocked.Exchange(ref _lastInitialMapPayloadBytes, payloadBytes);
        Interlocked.Exchange(ref _lastInitialMapTruncated, truncated ? 1 : 0);
    }

    // Section D - Concurrency & Parallel Scheduler Metrics
    private long _uiaReadQueued;
    private long _uiaReadActive;
    private long _uiaReadMaxConcurrent;
    private long _uiaReadQueueWaitMs;
    private long _uiaReadExecutionMs;
    private long _uiaReadTimeout;
    private long _uiaReadCancelled;
    private long _uiaReadLanePid;
    private long _uiaWriteQueued;
    private long _uiaWriteQueueWaitMs;
    private long _uiaWriteExecutionMs;
    private long _syncOverAsyncFallbackCount;
    private long _uiaPoisonedWorkers;
    private long _uiaWorkerReplacements;
    private long _uiaAbandonedWorkers;
    private long _uiaWorkerReplacementRejected;

    public long UiaReadQueued => Interlocked.Read(ref _uiaReadQueued);
    public long UiaReadActive => Interlocked.Read(ref _uiaReadActive);
    public long UiaReadMaxConcurrent => Interlocked.Read(ref _uiaReadMaxConcurrent);
    public long UiaReadQueueWaitMs => Interlocked.Read(ref _uiaReadQueueWaitMs);
    public long UiaReadExecutionMs => Interlocked.Read(ref _uiaReadExecutionMs);
    public long UiaReadTimeout => Interlocked.Read(ref _uiaReadTimeout);
    public long UiaReadCancelled => Interlocked.Read(ref _uiaReadCancelled);
    public long UiaReadLanePid => Interlocked.Read(ref _uiaReadLanePid);
    public long UiaWriteQueued => Interlocked.Read(ref _uiaWriteQueued);
    public long UiaWriteQueueWaitMs => Interlocked.Read(ref _uiaWriteQueueWaitMs);
    public long UiaWriteExecutionMs => Interlocked.Read(ref _uiaWriteExecutionMs);
    public long SyncOverAsyncFallbackCount => Interlocked.Read(ref _syncOverAsyncFallbackCount);
    public long UiaPoisonedWorkers => Interlocked.Read(ref _uiaPoisonedWorkers);
    public long UiaWorkerReplacements => Interlocked.Read(ref _uiaWorkerReplacements);
    public long UiaAbandonedWorkers => Interlocked.Read(ref _uiaAbandonedWorkers);
    public long UiaWorkerReplacementRejected => Interlocked.Read(ref _uiaWorkerReplacementRejected);

    public void IncUiaReadQueued() => Interlocked.Increment(ref _uiaReadQueued);
    public void DecUiaReadQueued() => Interlocked.Decrement(ref _uiaReadQueued);
    public void IncUiaReadActive()
    {
        var current = Interlocked.Increment(ref _uiaReadActive);
        long max;
        do
        {
            max = Interlocked.Read(ref _uiaReadMaxConcurrent);
            if (current <= max) break;
        } while (Interlocked.CompareExchange(ref _uiaReadMaxConcurrent, current, max) != max);
    }
    public void DecUiaReadActive() => Interlocked.Decrement(ref _uiaReadActive);
    public void AddUiaReadQueueWaitMs(double ms) => Interlocked.Add(ref _uiaReadQueueWaitMs, (long)Math.Round(ms));
    public void AddUiaReadExecutionMs(double ms) => Interlocked.Add(ref _uiaReadExecutionMs, (long)Math.Round(ms));
    public void IncUiaReadTimeout() => Interlocked.Increment(ref _uiaReadTimeout);
    public void IncUiaReadCancelled() => Interlocked.Increment(ref _uiaReadCancelled);
    public void SetUiaReadLanePid(int pid) => Interlocked.Exchange(ref _uiaReadLanePid, pid);
    public void IncUiaWriteQueued() => Interlocked.Increment(ref _uiaWriteQueued);
    public void DecUiaWriteQueued() => Interlocked.Decrement(ref _uiaWriteQueued);
    public void AddUiaWriteQueueWaitMs(double ms) => Interlocked.Add(ref _uiaWriteQueueWaitMs, (long)Math.Round(ms));
    public void AddUiaWriteExecutionMs(double ms) => Interlocked.Add(ref _uiaWriteExecutionMs, (long)Math.Round(ms));
    public void IncSyncOverAsyncFallback() => Interlocked.Increment(ref _syncOverAsyncFallbackCount);
    public void IncUiaPoisonedWorkers() => Interlocked.Increment(ref _uiaPoisonedWorkers);
    public void IncUiaWorkerReplacements() => Interlocked.Increment(ref _uiaWorkerReplacements);
    public void IncUiaAbandonedWorkers() => Interlocked.Increment(ref _uiaAbandonedWorkers);
    public void IncUiaWorkerReplacementRejected() => Interlocked.Increment(ref _uiaWorkerReplacementRejected);

    // Phase G - Session & Request Cache Telemetry
    public CacheTelemetry FindCache { get; } = new();
    public CacheTelemetry SnapshotCache { get; } = new();
    public CacheTelemetry HwndMetadata { get; } = new();
    public CacheTelemetry MonitorTopology { get; } = new();

    private long _windowSnapshotReuses;
    public long WindowSnapshotReuses => Interlocked.Read(ref _windowSnapshotReuses);
    public void IncWindowSnapshotReuses() => Interlocked.Increment(ref _windowSnapshotReuses);

    public long FindCacheHits => FindCache.HitCount;
    public long FindCacheMisses => FindCache.MissCount;
    public long SnapshotCacheHits => SnapshotCache.HitCount;
    public long SnapshotCacheMisses => SnapshotCache.MissCount;
    public long HwndMetadataHits => HwndMetadata.HitCount;
    public long HwndMetadataPidReuseRejects => HwndMetadata.PidReuseRejectCount;
    public long MonitorTopologyHits => MonitorTopology.HitCount;
    public long MonitorTopologyInvalidations => MonitorTopology.InvalidationCount;

    public Dictionary<string, long> GetSnapshot() => new()
    {
        ["duplicateReadSuppressed"] = DuplicateReadSuppressed,
        ["duplicateMutationRetrySuppressed"] = DuplicateMutationRetrySuppressed,
        ["postVerifiedObservation"] = PostVerifiedObservation,
        ["launchFollowupDiscovery"] = LaunchFollowupDiscovery,
        ["closeRetry"] = CloseRetry,
        ["appsThenLaunchWithin5s"] = AppsThenLaunchWithin5s,
        ["launchPrecededByApps"] = AppsThenLaunchWithin5s,
        ["sequentialSingleActionRunLength"] = SequentialSingleActionRunLength,
        ["observedChangeFollowup"] = ObservedChangeFollowup,
        ["initialMapBuildCount"] = InitialMapBuildCount,
        ["initialMapBuildTotalMs"] = InitialMapBuildTotalMs,
        ["initialMapNodesVisitedTotal"] = InitialMapNodesVisitedTotal,
        ["initialMapComReadsTotal"] = InitialMapComReadsTotal,
        ["initialMapActionablesTotal"] = InitialMapActionablesTotal,
        ["initialMapLandmarksTotal"] = InitialMapLandmarksTotal,
        ["initialMapPayloadBytesTotal"] = InitialMapPayloadBytesTotal,
        ["initialMapTruncatedCount"] = InitialMapTruncatedCount,
        ["lastInitialMapBuildMs"] = LastInitialMapBuildMs,
        ["lastInitialMapNodesVisited"] = LastInitialMapNodesVisited,
        ["lastInitialMapPayloadBytes"] = LastInitialMapPayloadBytes,
        ["lastInitialMapTruncated"] = _lastInitialMapTruncated,
        ["uiaReadQueued"] = UiaReadQueued,
        ["uiaReadActive"] = UiaReadActive,
        ["uiaReadMaxConcurrent"] = UiaReadMaxConcurrent,
        ["uiaReadQueueWaitMs"] = UiaReadQueueWaitMs,
        ["uiaReadExecutionMs"] = UiaReadExecutionMs,
        ["uiaReadTimeout"] = UiaReadTimeout,
        ["uiaReadCancelled"] = UiaReadCancelled,
        ["uiaReadLanePid"] = UiaReadLanePid,
        ["uiaWriteQueued"] = UiaWriteQueued,
        ["uiaWriteQueueWaitMs"] = UiaWriteQueueWaitMs,
        ["uiaWriteExecutionMs"] = UiaWriteExecutionMs,
        ["syncOverAsyncFallbackCount"] = SyncOverAsyncFallbackCount,
        ["uiaPoisonedWorkers"] = UiaPoisonedWorkers,
        ["uiaWorkerReplacements"] = UiaWorkerReplacements,
        ["uiaAbandonedWorkers"] = UiaAbandonedWorkers,
        ["uiaWorkerReplacementRejected"] = UiaWorkerReplacementRejected,
        ["findCacheHits"] = FindCacheHits,
        ["findCacheMisses"] = FindCacheMisses,
        ["snapshotCacheHits"] = SnapshotCacheHits,
        ["snapshotCacheMisses"] = SnapshotCacheMisses,
        ["windowSnapshotReuses"] = WindowSnapshotReuses,
        ["hwndMetadataHits"] = HwndMetadataHits,
        ["hwndMetadataPidReuseRejects"] = HwndMetadataPidReuseRejects,
        ["monitorTopologyHits"] = MonitorTopologyHits,
        ["monitorTopologyInvalidations"] = MonitorTopologyInvalidations,
    };
}

