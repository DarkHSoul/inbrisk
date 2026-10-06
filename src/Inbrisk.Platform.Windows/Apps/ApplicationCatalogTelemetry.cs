using System.Threading;

namespace Inbrisk.Platform.Windows.Apps;

/// <summary>
/// Internal telemetry counters for Application Catalog and Launch-Resolution Cache.
/// Phase G Round 2: These counters are internal and testable, not model-visible.
/// </summary>
public sealed class ApplicationCatalogTelemetry
{
    private long _catalogMemoryHits;
    private long _catalogDiskLoads;
    private long _catalogDiskLoadFailures;
    private long _catalogColdBuilds;
    private long _catalogWarmStarts;
    private long _catalogRefreshes;
    private long _catalogRefreshFailures;
    private long _catalogTtlInvalidations;
    private long _catalogWatcherInvalidations;
    private long _catalogWatcherOverflowInvalidations;
    private long _catalogStalePublishRejects;
    private long _catalogSingleFlightReuses;
    private long _catalogRefreshLockWaits;
    private long _catalogRefreshLockTimeouts;
    private long _catalogCrossProcessReuseCount;

    private long _launchResolutionHits;
    private long _launchResolutionMisses;
    private long _launchResolutionInserts;
    private long _launchResolutionInvalidations;
    private long _launchResolutionStaleIdentityEvictions;
    private long _launchResolutionReResolutions;

    private int _refreshInFlightCount;
    private long _catalogGeneration;
    private int _catalogEntryCount;
    private int _launchResolutionEntryCount;

    public long CatalogMemoryHits => Volatile.Read(ref _catalogMemoryHits);
    public long CatalogDiskLoads => Volatile.Read(ref _catalogDiskLoads);
    public long CatalogDiskLoadFailures => Volatile.Read(ref _catalogDiskLoadFailures);
    public long CatalogColdBuilds => Volatile.Read(ref _catalogColdBuilds);
    public long CatalogWarmStarts => Volatile.Read(ref _catalogWarmStarts);
    public long CatalogRefreshes => Volatile.Read(ref _catalogRefreshes);
    public long CatalogRefreshFailures => Volatile.Read(ref _catalogRefreshFailures);
    public long CatalogTtlInvalidations => Volatile.Read(ref _catalogTtlInvalidations);
    public long CatalogWatcherInvalidations => Volatile.Read(ref _catalogWatcherInvalidations);
    public long CatalogWatcherOverflowInvalidations => Volatile.Read(ref _catalogWatcherOverflowInvalidations);
    public long CatalogStalePublishRejects => Volatile.Read(ref _catalogStalePublishRejects);
    public long CatalogSingleFlightReuses => Volatile.Read(ref _catalogSingleFlightReuses);
    public long CatalogRefreshLockWaits => Volatile.Read(ref _catalogRefreshLockWaits);
    public long CatalogRefreshLockTimeouts => Volatile.Read(ref _catalogRefreshLockTimeouts);
    public long CatalogCrossProcessReuseCount => Volatile.Read(ref _catalogCrossProcessReuseCount);

    public long LaunchResolutionHits => Volatile.Read(ref _launchResolutionHits);
    public long LaunchResolutionMisses => Volatile.Read(ref _launchResolutionMisses);
    public long LaunchResolutionInserts => Volatile.Read(ref _launchResolutionInserts);
    public long LaunchResolutionInvalidations => Volatile.Read(ref _launchResolutionInvalidations);
    public long LaunchResolutionStaleIdentityEvictions => Volatile.Read(ref _launchResolutionStaleIdentityEvictions);
    public long LaunchResolutionReResolutions => Volatile.Read(ref _launchResolutionReResolutions);

    public int RefreshInFlightCount => Volatile.Read(ref _refreshInFlightCount);
    public long CatalogGeneration => Volatile.Read(ref _catalogGeneration);
    public int CatalogEntryCount => Volatile.Read(ref _catalogEntryCount);
    public int LaunchResolutionEntryCount => Volatile.Read(ref _launchResolutionEntryCount);

    public void IncCatalogMemoryHits() => Interlocked.Increment(ref _catalogMemoryHits);
    public void IncCatalogDiskLoads() => Interlocked.Increment(ref _catalogDiskLoads);
    public void IncCatalogDiskLoadFailures() => Interlocked.Increment(ref _catalogDiskLoadFailures);
    public void IncCatalogColdBuilds() => Interlocked.Increment(ref _catalogColdBuilds);
    public void IncCatalogWarmStarts() => Interlocked.Increment(ref _catalogWarmStarts);
    public void IncCatalogRefreshes() => Interlocked.Increment(ref _catalogRefreshes);
    public void IncCatalogRefreshFailures() => Interlocked.Increment(ref _catalogRefreshFailures);
    public void IncCatalogTtlInvalidations() => Interlocked.Increment(ref _catalogTtlInvalidations);
    public void IncCatalogWatcherInvalidations() => Interlocked.Increment(ref _catalogWatcherInvalidations);
    public void IncCatalogWatcherOverflowInvalidations() => Interlocked.Increment(ref _catalogWatcherOverflowInvalidations);
    public void IncCatalogStalePublishRejects() => Interlocked.Increment(ref _catalogStalePublishRejects);
    public void IncCatalogSingleFlightReuses() => Interlocked.Increment(ref _catalogSingleFlightReuses);
    public void IncCatalogRefreshLockWaits() => Interlocked.Increment(ref _catalogRefreshLockWaits);
    public void IncCatalogRefreshLockTimeouts() => Interlocked.Increment(ref _catalogRefreshLockTimeouts);
    public void IncCatalogCrossProcessReuseCount() => Interlocked.Increment(ref _catalogCrossProcessReuseCount);

    public void IncLaunchResolutionHits() => Interlocked.Increment(ref _launchResolutionHits);
    public void IncLaunchResolutionMisses() => Interlocked.Increment(ref _launchResolutionMisses);
    public void IncLaunchResolutionInserts() => Interlocked.Increment(ref _launchResolutionInserts);
    public void IncLaunchResolutionInvalidations() => Interlocked.Increment(ref _launchResolutionInvalidations);
    public void IncLaunchResolutionStaleIdentityEvictions() => Interlocked.Increment(ref _launchResolutionStaleIdentityEvictions);
    public void IncLaunchResolutionReResolutions() => Interlocked.Increment(ref _launchResolutionReResolutions);

    public void EnterRefresh() => Interlocked.Increment(ref _refreshInFlightCount);
    public void ExitRefresh() => Interlocked.Decrement(ref _refreshInFlightCount);
    public void SetCatalogGeneration(long gen) => Interlocked.Exchange(ref _catalogGeneration, gen);
    public void SetCatalogEntryCount(int count) => Interlocked.Exchange(ref _catalogEntryCount, count);
    public void SetLaunchResolutionEntryCount(int count) => Interlocked.Exchange(ref _launchResolutionEntryCount, count);

    public void Reset()
    {
        Interlocked.Exchange(ref _catalogMemoryHits, 0);
        Interlocked.Exchange(ref _catalogDiskLoads, 0);
        Interlocked.Exchange(ref _catalogDiskLoadFailures, 0);
        Interlocked.Exchange(ref _catalogColdBuilds, 0);
        Interlocked.Exchange(ref _catalogWarmStarts, 0);
        Interlocked.Exchange(ref _catalogRefreshes, 0);
        Interlocked.Exchange(ref _catalogRefreshFailures, 0);
        Interlocked.Exchange(ref _catalogTtlInvalidations, 0);
        Interlocked.Exchange(ref _catalogWatcherInvalidations, 0);
        Interlocked.Exchange(ref _catalogWatcherOverflowInvalidations, 0);
        Interlocked.Exchange(ref _catalogStalePublishRejects, 0);
        Interlocked.Exchange(ref _catalogSingleFlightReuses, 0);
        Interlocked.Exchange(ref _catalogRefreshLockWaits, 0);
        Interlocked.Exchange(ref _catalogRefreshLockTimeouts, 0);
        Interlocked.Exchange(ref _catalogCrossProcessReuseCount, 0);

        Interlocked.Exchange(ref _launchResolutionHits, 0);
        Interlocked.Exchange(ref _launchResolutionMisses, 0);
        Interlocked.Exchange(ref _launchResolutionInserts, 0);
        Interlocked.Exchange(ref _launchResolutionInvalidations, 0);
        Interlocked.Exchange(ref _launchResolutionStaleIdentityEvictions, 0);
        Interlocked.Exchange(ref _launchResolutionReResolutions, 0);

        Interlocked.Exchange(ref _refreshInFlightCount, 0);
        Interlocked.Exchange(ref _catalogGeneration, 0);
        Interlocked.Exchange(ref _catalogEntryCount, 0);
        Interlocked.Exchange(ref _launchResolutionEntryCount, 0);
    }
}
