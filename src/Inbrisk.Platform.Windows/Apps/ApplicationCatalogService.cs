using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Inbrisk.Core;

namespace Inbrisk.Platform.Windows.Apps;

/// <summary>
/// Service managing the Persistent Application Catalog on disk and in memory.
/// Supports atomic writes, corrupt cache recovery, warm startup, 24h safety TTL,
/// Start Menu FileSystemWatchers with event-storm coalescing, overflow handling,
/// single-flight refreshes, and stale publication rejection.
/// </summary>
public sealed class ApplicationCatalogService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _storageDir;
    private readonly string _catalogFilePath;
    private readonly string _lockFilePath;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<(IReadOnlyList<AppInfo> Apps, IReadOnlyList<AppCatalogEntryDto> Entries, string? Fingerprint)> _enumerator;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly List<string> _startMenuDirs = new();
    private readonly Timer _debounceTimer;
    private readonly CancellationTokenSource _cts = new();

    private ApplicationCatalogSnapshot? _currentSnapshot;
    private long _dirtyGeneration = 1;
    private Task<ApplicationCatalogSnapshot>? _currentRefreshTask;
    private readonly object _refreshLock = new();
    private bool _isDisposed;

    public ApplicationCatalogTelemetry Telemetry { get; }
    public string StorageDirectory => _storageDir;
    public string CatalogFilePath => _catalogFilePath;
    public string LockFilePath => _lockFilePath;
    public long CurrentGeneration => Volatile.Read(ref _dirtyGeneration);
    public string CurrentSnapshotId => _currentSnapshot?.SnapshotId ?? "";

    public event Action<long>? CatalogChanged;
    public event Action<long, string>? CatalogSnapshotChanged;

    public ApplicationCatalogService(
        string? storageDirectory = null,
        Func<DateTimeOffset>? clock = null,
        ApplicationCatalogTelemetry? telemetry = null,
        Func<(IReadOnlyList<AppInfo> Apps, IReadOnlyList<AppCatalogEntryDto> Entries, string? Fingerprint)>? enumerator = null,
        IEnumerable<string>? startMenuDirs = null)
    {
        _storageDir = storageDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "inbrisk");
        _catalogFilePath = Path.Combine(_storageDir, "app-catalog.json");
        _lockFilePath = Path.Combine(_storageDir, "app_catalog.lock");
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        Telemetry = telemetry ?? new ApplicationCatalogTelemetry();
        _enumerator = enumerator ?? EnumerateDefault;

        _debounceTimer = new Timer(OnDebounceTimerElapsed, null, Timeout.Infinite, Timeout.Infinite);

        // Initialize Start Menu watchers if provided
        if (startMenuDirs != null)
        {
            foreach (var dir in startMenuDirs)
            {
                _startMenuDirs.Add(dir);
                AttachWatcher(dir);
            }
        }
    }

    public void AttachWatcher(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                var fsw = new FileSystemWatcher(dir, "*.lnk")
                {
                    IncludeSubdirectories = true,
                    EnableRaisingEvents = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
                };
                fsw.Changed += (_, _) => OnWatcherEvent();
                fsw.Created += (_, _) => OnWatcherEvent();
                fsw.Deleted += (_, _) => OnWatcherEvent();
                fsw.Renamed += (_, _) => OnWatcherEvent();
                fsw.Error += (_, e) => OnWatcherError(e);
                _watchers.Add(fsw);
            }
        }
        catch { }
    }

    private void OnWatcherEvent()
    {
        if (_isDisposed) return;
        Telemetry.IncCatalogWatcherInvalidations();
        Interlocked.Increment(ref _dirtyGeneration);
        // Coalesce / debounce event storms (100ms)
        _debounceTimer.Change(100, Timeout.Infinite);
    }

    private void OnWatcherError(ErrorEventArgs e)
    {
        if (_isDisposed) return;
        Telemetry.IncCatalogWatcherOverflowInvalidations();
        Interlocked.Increment(ref _dirtyGeneration);
        // Force full refresh immediately on overflow or watcher error
        _debounceTimer.Change(0, Timeout.Infinite);
    }

    private void OnDebounceTimerElapsed(object? state)
    {
        if (_isDisposed) return;
        try
        {
            _ = RefreshAsync();
        }
        catch { }
    }

    /// <summary>
    /// Explicitly triggers a source-scoped or global invalidation.
    /// </summary>
    public void Invalidate(string? reason = null)
    {
        Interlocked.Increment(ref _dirtyGeneration);
        _debounceTimer.Change(10, Timeout.Infinite);
    }

    /// <summary>
    /// Simulates a watcher buffer overflow / error for testing.
    /// </summary>
    public void TriggerWatcherOverflowForTest()
    {
        OnWatcherError(new ErrorEventArgs(new InternalBufferOverflowException("Test overflow")));
    }

    /// <summary>
    /// Simulates a watcher shortcut event for testing.
    /// </summary>
    public void TriggerWatcherEventForTest()
    {
        OnWatcherEvent();
    }

    /// <summary>
    /// Returns the active catalog snapshot, loading from disk (warm) or building (cold) if needed.
    /// Enforces 24-hour safety TTL based strictly on authoritative persisted creation age.
    /// </summary>
    public ApplicationCatalogSnapshot GetSnapshot()
    {
        var snapshot = _currentSnapshot;

        // Check if cached snapshot is valid
        if (snapshot != null)
        {
            var age = _clock() - snapshot.CreatedAtUtc;
            if (age >= ApplicationCatalogConstants.SafetyTtl)
            {
                Telemetry.IncCatalogTtlInvalidations();
                Interlocked.Increment(ref _dirtyGeneration);
                // Expired TTL requires rebuild
                return RefreshAsync().GetAwaiter().GetResult();
            }

            if (snapshot.Generation == Volatile.Read(ref _dirtyGeneration))
            {
                using var _ = PerfTrace.Stage("catalog=memory_hit");
                Telemetry.IncCatalogMemoryHits();
                Interlocked.Increment(ref AppService.MemoryCacheHitCount);
                return snapshot;
            }
        }

        // Try warm disk load if no snapshot is loaded yet and not dirtied
        if (snapshot == null && Volatile.Read(ref _dirtyGeneration) == 1)
        {
            if (TryLoadWarmFromDisk(out var warmSnapshot) && warmSnapshot != null)
            {
                using var _ = PerfTrace.Stage("catalog=disk_warm");
                _currentSnapshot = warmSnapshot;
                Telemetry.SetCatalogGeneration(warmSnapshot.Generation);
                Telemetry.SetCatalogEntryCount(warmSnapshot.Apps.Count);
                return warmSnapshot;
            }
        }

        // Needs refresh or cold build
        return RefreshAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Asynchronously performs single-flight refresh with stale publication rejection.
    /// </summary>
    public Task<ApplicationCatalogSnapshot> RefreshAsync(CancellationToken ct = default)
    {
        lock (_refreshLock)
        {
            if (_currentRefreshTask != null && !_currentRefreshTask.IsCompleted)
            {
                Telemetry.IncCatalogSingleFlightReuses();
                return _currentRefreshTask;
            }

            _currentRefreshTask = ProduceRefreshAsync();
            return _currentRefreshTask;
        }
    }

    private async Task<ApplicationCatalogSnapshot> ProduceRefreshAsync()
    {
        long startingDirtyGen = Volatile.Read(ref _dirtyGeneration);
        Telemetry.EnterRefresh();
        try
        {
            var isCold = (_currentSnapshot == null);
            using var _ = isCold ? PerfTrace.Stage("catalog=cold_build") : PerfTrace.Stage("catalog=refresh");

            long diskRevBeforeLock = ReadDiskRevision();

            // 1. Acquire cross-process exclusive refresh/persistence lock (Option A: dedicated file lock)
            using var crossProcessLock = await AcquireCrossProcessLockAsync(5000, _cts.Token).ConfigureAwait(false);
            if (crossProcessLock == null)
            {
                // Bounded lock acquisition timed out!
                if (_currentSnapshot != null)
                {
                    return _currentSnapshot;
                }
                if (TryLoadWarmFromDisk(out var warm) && warm != null)
                {
                    _currentSnapshot = warm;
                    return warm;
                }
                throw new TimeoutException("Timed out waiting for cross-process refresh lock.");
            }

            // 2. Cross-Process Single-Flight / Reuse Check
            // Re-read disk metadata under the lock. If another process refreshed and published a valid,
            // fresh snapshot while we were waiting for the lock (currentDiskRev > diskRevBeforeLock),
            // reuse it to eliminate redundant scans.
            long currentDiskRev = ReadDiskRevision();
            if (currentDiskRev > diskRevBeforeLock && TryLoadWarmFromDisk(out var candidate) && candidate != null)
            {
                var age = _clock() - candidate.CreatedAtUtc;
                if (age < ApplicationCatalogConstants.SafetyTtl)
                {
                    Telemetry.IncCatalogCrossProcessReuseCount();
                    _currentSnapshot = candidate;
                    return candidate;
                }
            }

            // 3. Authoritative Source Capture INSIDE the cross-process lock
            // Invariant: No process can capture authoritative application sources for publication
            // before acquiring the exclusive cross-process lock.
            (IReadOnlyList<AppInfo> apps, IReadOnlyList<AppCatalogEntryDto> entries, string? fingerprint) data;
            try
            {
                data = await Task.Run(() => _enumerator(), _cts.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                Telemetry.IncCatalogRefreshFailures();
                if (_currentSnapshot != null)
                {
                    // Preserve existing valid snapshot on transient enumeration failure
                    return _currentSnapshot;
                }
                throw;
            }

            // 4. Check for in-process publication race
            if (Volatile.Read(ref _dirtyGeneration) != startingDirtyGen)
            {
                Telemetry.IncCatalogStalePublishRejects();
                if (_currentSnapshot != null)
                    return _currentSnapshot;
            }

            var now = _clock();
            var snapshotId = Guid.NewGuid().ToString("N");
            var sourceTimestamp = GetLatestSourceTimestamp(_startMenuDirs);

            // 5. Monotonic Sequence Ordering under the lock:
            // Revision is strictly assigned as diskRev + 1
            long diskRev = ReadDiskRevision();
            long nextRev = diskRev + 1;

            var newSnapshot = new ApplicationCatalogSnapshot(
                data.apps,
                data.entries,
                startingDirtyGen,
                now,
                snapshotId,
                sourceTimestamp,
                data.fingerprint,
                nextRev);

            _currentSnapshot = newSnapshot;
            Telemetry.SetCatalogGeneration(startingDirtyGen);
            Telemetry.SetCatalogEntryCount(data.apps.Count);

            if (isCold)
                Telemetry.IncCatalogColdBuilds();
            else
                Telemetry.IncCatalogRefreshes();

            // 6. Atomically persist to disk while holding lock
            SaveCatalogToDisk(newSnapshot, lockAlreadyHeld: true);

            // 7. Notify listeners (e.g. LaunchResolutionCache)
            try
            {
                CatalogChanged?.Invoke(startingDirtyGen);
            }
            catch { }
            try
            {
                CatalogSnapshotChanged?.Invoke(startingDirtyGen, snapshotId);
            }
            catch { }

            return newSnapshot;
        }
        finally
        {
            Telemetry.ExitRefresh();
        }
    }

    private bool TryLoadWarmFromDisk(out ApplicationCatalogSnapshot? snapshot)
    {
        snapshot = null;
        if (!File.Exists(_catalogFilePath))
            return false;

        Telemetry.IncCatalogDiskLoads();
        try
        {
            using var stream = new FileStream(_catalogFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var dto = JsonSerializer.Deserialize<AppCatalogDto>(stream, JsonOptions);

            if (dto == null || dto.FormatVersion != ApplicationCatalogConstants.PersistentCatalogFormatVersion)
            {
                Telemetry.IncCatalogDiskLoadFailures();
                return false;
            }

            // Authoritative persistent age check: Now - PersistedCreatedAtUtc
            var age = _clock() - dto.CreatedAtUtc;
            if (age >= ApplicationCatalogConstants.SafetyTtl)
            {
                Telemetry.IncCatalogTtlInvalidations();
                return false;
            }

            // Offline source changes check: if Start Menu directories were modified while Inbrisk was stopped
            if (dto.SourceTimestampUtc.HasValue && _startMenuDirs.Count > 0)
            {
                var currentSourceTs = GetLatestSourceTimestamp(_startMenuDirs);
                if (currentSourceTs.HasValue && currentSourceTs.Value > dto.SourceTimestampUtc.Value)
                {
                    Telemetry.IncCatalogWatcherInvalidations();
                    return false;
                }
            }

            var apps = dto.Entries.Select(e => new AppInfo(e.Name, e.Method, e.Launch, e.Kind)).ToList();
            var snapshotId = string.IsNullOrEmpty(dto.SnapshotId) ? Guid.NewGuid().ToString("N") : dto.SnapshotId;
            snapshot = new ApplicationCatalogSnapshot(
                apps,
                dto.Entries,
                dto.Generation,
                dto.CreatedAtUtc,
                snapshotId,
                dto.SourceTimestampUtc,
                dto.Fingerprint,
                dto.Revision);

            // Sync dirty generation
            if (dto.Generation > Volatile.Read(ref _dirtyGeneration))
                Interlocked.Exchange(ref _dirtyGeneration, dto.Generation);

            Telemetry.IncCatalogWarmStarts();
            return true;
        }
        catch (Exception)
        {
            Telemetry.IncCatalogDiskLoadFailures();
            return false;
        }
    }

    /// <summary>
    /// Acquires an exclusive cross-process file lock beside the cache path.
    /// Natural scope matches the cache filesystem directory across Windows sessions and processes.
    /// OS automatically releases lock on process crash or handle close.
    /// </summary>
    public async Task<IDisposable?> AcquireCrossProcessLockAsync(int timeoutMs = 5000, CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(_storageDir);
        }
        catch { }

        var sw = Stopwatch.StartNew();
        bool waited = false;
        while (sw.ElapsedMilliseconds < timeoutMs && !ct.IsCancellationRequested)
        {
            try
            {
                var fs = new FileStream(_lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                if (waited)
                {
                    Telemetry.IncCatalogRefreshLockWaits();
                }
                return fs;
            }
            catch (IOException)
            {
                waited = true;
                try
                {
                    await Task.Delay(20, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            catch (UnauthorizedAccessException)
            {
                waited = true;
                try
                {
                    await Task.Delay(20, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        Telemetry.IncCatalogRefreshLockTimeouts();
        return null;
    }

    /// <summary>
    /// Synchronously acquires an exclusive cross-process file lock beside the cache path.
    /// </summary>
    public IDisposable? AcquireCrossProcessLock(int timeoutMs = 5000)
    {
        try
        {
            Directory.CreateDirectory(_storageDir);
        }
        catch { }

        var sw = Stopwatch.StartNew();
        bool waited = false;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                var fs = new FileStream(_lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                if (waited)
                {
                    Telemetry.IncCatalogRefreshLockWaits();
                }
                return fs;
            }
            catch (IOException)
            {
                waited = true;
                Thread.Sleep(20);
            }
            catch (UnauthorizedAccessException)
            {
                waited = true;
                Thread.Sleep(20);
            }
        }

        Telemetry.IncCatalogRefreshLockTimeouts();
        return null;
    }

    /// <summary>
    /// Reads the persisted revision directly from the catalog file on disk.
    /// Returns 0 if missing, corrupt, or unsupported format version.
    /// </summary>
    public long ReadDiskRevision()
    {
        if (!File.Exists(_catalogFilePath))
            return 0;
        try
        {
            using var s = new FileStream(_catalogFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var d = JsonSerializer.Deserialize<AppCatalogDto>(s, JsonOptions);
            if (d == null || d.FormatVersion != ApplicationCatalogConstants.PersistentCatalogFormatVersion)
                return 0;
            return d.Revision;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Atomically persists catalog snapshot to disk using temp-write-and-replace,
    /// guarded by compare-before-replace to ensure older snapshots cannot overwrite newer publications.
    /// </summary>
    public void SaveCatalogToDisk(ApplicationCatalogSnapshot snapshot, bool lockAlreadyHeld = false)
    {
        try
        {
            Directory.CreateDirectory(_storageDir);
            var dto = new AppCatalogDto
            {
                FormatVersion = ApplicationCatalogConstants.PersistentCatalogFormatVersion,
                SnapshotId = snapshot.SnapshotId,
                Generation = snapshot.Generation,
                Revision = snapshot.Revision,
                CreatedAtUtc = snapshot.CreatedAtUtc,
                SourceTimestampUtc = snapshot.SourceTimestampUtc,
                Fingerprint = snapshot.Fingerprint,
                Entries = snapshot.Entries.ToList()
            };

            var tempFilePath = Path.Combine(_storageDir, $"app-catalog.json.tmp.{Guid.NewGuid():N}");
            using (var stream = new FileStream(tempFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, dto, JsonOptions);
                stream.Flush(true);
            }

            // Cross-process serialization via dedicated cache lock file (Option A)
            IDisposable? lockDisposable = null;
            try
            {
                if (!lockAlreadyHeld)
                {
                    lockDisposable = AcquireCrossProcessLock(2000);
                }

                // Compare-before-replace: do not overwrite a newer snapshot on disk with an older snapshot
                if (File.Exists(_catalogFilePath))
                {
                    try
                    {
                        using var readStream = new FileStream(_catalogFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        var existingDto = JsonSerializer.Deserialize<AppCatalogDto>(readStream, JsonOptions);
                        if (existingDto != null)
                        {
                            // Stale writer commit check:
                            // If existing on-disk catalog has a higher Revision, or equal Revision with different SnapshotId,
                            // or equal Revision with older timestamp, reject the stale writer commit!
                            if (existingDto.Revision > snapshot.Revision ||
                               (existingDto.Revision == snapshot.Revision && existingDto.SnapshotId != snapshot.SnapshotId) ||
                               (existingDto.Revision == snapshot.Revision && existingDto.Generation > snapshot.Generation) ||
                               (existingDto.Revision == snapshot.Revision && existingDto.Generation == snapshot.Generation && existingDto.CreatedAtUtc > snapshot.CreatedAtUtc))
                            {
                                Telemetry.IncCatalogStalePublishRejects();
                                try { File.Delete(tempFilePath); } catch { }
                                return;
                            }
                        }
                    }
                    catch { /* if read fails or file is corrupt, proceed with safe replace */ }
                }

                // Retry up to 5 times for atomic replace if a reader is currently reading
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        File.Move(tempFilePath, _catalogFilePath, overwrite: true);
                        break;
                    }
                    catch (IOException) when (attempt < 4)
                    {
                        Thread.Sleep(5);
                    }
                }
            }
            finally
            {
                lockDisposable?.Dispose();
                if (File.Exists(tempFilePath))
                {
                    try { File.Delete(tempFilePath); } catch { }
                }
            }
        }
        catch { }
    }

    private static DateTimeOffset? GetLatestSourceTimestamp(IEnumerable<string>? dirs)
    {
        if (dirs == null) return null;
        DateTimeOffset? latest = null;
        foreach (var dir in dirs)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    var dt = new DirectoryInfo(dir).LastWriteTimeUtc;
                    if (!latest.HasValue || dt > latest.Value)
                        latest = dt;
                }
            }
            catch { }
        }
        return latest;
    }

    private static (IReadOnlyList<AppInfo> Apps, IReadOnlyList<AppCatalogEntryDto> Entries, string? Fingerprint) EnumerateDefault()
    {
        return (Array.Empty<AppInfo>(), Array.Empty<AppCatalogEntryDto>(), null);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        try { _cts.Cancel(); } catch { }
        try { _debounceTimer.Dispose(); } catch { }

        foreach (var w in _watchers)
        {
            try { w.EnableRaisingEvents = false; w.Dispose(); } catch { }
        }
        _watchers.Clear();

        Task? task;
        lock (_refreshLock) task = _currentRefreshTask;
        if (task != null && !task.IsCompleted)
        {
            try { task.Wait(300); } catch { }
        }

        try { _cts.Dispose(); } catch { }
    }
}
