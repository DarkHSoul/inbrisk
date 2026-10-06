using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Inbrisk.Core;

namespace Inbrisk.Platform.Windows.Apps;

/// <summary>
/// Persistent Launch-Resolution Cache.
/// Maps normalized launch requests to resolved application identities.
/// Features:
/// - Stale identity self-healing (evict + re-resolve if target is missing, App Paths changed, or package uninstalled)
/// - Ambiguity preservation (never caches ambiguous outcomes as winners)
/// - Snapshot-identity coherence linkage with ApplicationCatalogService (SnapshotId)
/// - Single-flight concurrent miss resolution
/// - Bounded one-shot authoritative refresh on NotFound to discover newly installed applications
/// - Atomic disk persistence to launch-cache.json
/// - Deterministic argument digesting (SHA-256) for cross-process determinism
/// </summary>
public sealed class LaunchResolutionCache : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _storageDir;
    private readonly string _cacheFilePath;
    private readonly ApplicationCatalogService _catalogService;
    private readonly ApplicationCatalogTelemetry _telemetry;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<LaunchCacheEntryDto, bool> _identityValidator;
    private readonly Func<string, bool>? _packageValidator;
    private readonly Func<string, string?>? _appPathResolver;

    private readonly ConcurrentDictionary<LaunchResolutionKey, LaunchCacheEntryDto> _entries = new();
    private readonly Dictionary<LaunchResolutionKey, Task<(AppService.ResolvedApp? Best, bool IsAmbiguous, List<AppService.ResolvedApp>? Candidates)>> _inFlightMisses = new();
    private readonly object _inFlightLock = new();

    public ApplicationCatalogTelemetry Telemetry => _telemetry;
    public string CacheFilePath => _cacheFilePath;
    public int EntryCount => _entries.Count;

    public LaunchResolutionCache(
        ApplicationCatalogService catalogService,
        string? storageDirectory = null,
        Func<DateTimeOffset>? clock = null,
        ApplicationCatalogTelemetry? telemetry = null,
        Func<LaunchCacheEntryDto, bool>? identityValidator = null,
        Func<string, bool>? packageValidator = null,
        Func<string, string?>? appPathResolver = null)
    {
        _catalogService = catalogService;
        _storageDir = storageDirectory ?? catalogService.StorageDirectory;
        _cacheFilePath = Path.Combine(_storageDir, "launch-cache.json");
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _telemetry = telemetry ?? catalogService.Telemetry;
        _packageValidator = packageValidator;
        _appPathResolver = appPathResolver;
        _identityValidator = identityValidator ?? ValidateIdentity;

        // Subscribe to catalog changes for snapshot-linked invalidation
        _catalogService.CatalogChanged += OnCatalogChanged;
        _catalogService.CatalogSnapshotChanged += OnCatalogSnapshotChanged;

        // Try load from disk
        TryLoadFromDisk();
    }

    private void OnCatalogChanged(long newGeneration)
    {
        InvalidateStaleEntries();
    }

    private void OnCatalogSnapshotChanged(long newGeneration, string newSnapshotId)
    {
        InvalidateStaleEntries(newSnapshotId);
    }

    private void InvalidateStaleEntries(string? targetSnapshotId = null)
    {
        var activeSnapshotId = targetSnapshotId ?? _catalogService.CurrentSnapshotId;
        var toEvict = _entries
            .Where(kvp => !string.IsNullOrEmpty(activeSnapshotId) && kvp.Value.CatalogSnapshotId != activeSnapshotId)
            .ToList();

        foreach (var kvp in toEvict)
        {
            if (_entries.TryRemove(kvp.Key, out _))
            {
                _telemetry.IncLaunchResolutionInvalidations();
            }
        }
        lock (_inFlightLock)
        {
            _inFlightMisses.Clear();
        }
        _telemetry.SetLaunchResolutionEntryCount(_entries.Count);
    }

    /// <summary>
    /// Checks the cache or resolves using the provided resolver function.
    /// Preserves ambiguity (never caches if ambiguous or unresolved).
    /// Supports bounded one-shot authoritative refresh on genuine NotFound.
    /// </summary>
    public async Task<(AppService.ResolvedApp? Resolved, bool IsAmbiguous, List<AppService.ResolvedApp>? AmbiguousCandidates)> GetOrResolveAsync(
        string appName,
        bool exactOnly,
        bool newInstance,
        Func<Task<(AppService.ResolvedApp? Best, bool IsAmbiguous, List<AppService.ResolvedApp>? Candidates)>> resolver,
        string? arguments = null)
    {
        var key = new LaunchResolutionKey(appName, exactOnly, newInstance, arguments);
        var currentSnapshot = _catalogService.GetSnapshot();
        var currentSnapshotId = currentSnapshot.SnapshotId;

        // 1. Check in-memory cache
        if (_entries.TryGetValue(key, out var cached))
        {
            // Generation / SnapshotId coherence check: prevents ABA collisions across process restarts
            if (!string.IsNullOrEmpty(currentSnapshotId) &&
                !string.IsNullOrEmpty(cached.CatalogSnapshotId) &&
                cached.CatalogSnapshotId != currentSnapshotId)
            {
                _entries.TryRemove(key, out _);
                lock (_inFlightLock) { _inFlightMisses.Remove(key); }
                _telemetry.IncLaunchResolutionInvalidations();
                _telemetry.SetLaunchResolutionEntryCount(_entries.Count);
            }
            else
            {
                // Self-healing identity check
                if (_identityValidator(cached))
                {
                    using var hitTrace = PerfTrace.Stage("launch_resolution=hit");
                    _telemetry.IncLaunchResolutionHits();
                    var app = new AppService.ResolvedApp(
                        cached.Method,
                        cached.Identifier,
                        cached.DisplayName,
                        cached.ExeHints,
                        cached.Score);
                    return (app, false, null);
                }
                else
                {
                    // Stale identity! Evict and re-resolve
                    using var evictTrace = PerfTrace.Stage("launch_resolution=stale_evict");
                    _entries.TryRemove(key, out _);
                    lock (_inFlightLock) { _inFlightMisses.Remove(key); }
                    _telemetry.IncLaunchResolutionStaleIdentityEvictions();
                    _telemetry.IncLaunchResolutionReResolutions();
                    _telemetry.SetLaunchResolutionEntryCount(_entries.Count);
                }
            }
        }

        // 2. Miss path — single flight per key
        Task<(AppService.ResolvedApp? Best, bool IsAmbiguous, List<AppService.ResolvedApp>? Candidates)> task;
        lock (_inFlightLock)
        {
            if (_entries.TryGetValue(key, out var doubleCheckedCached))
            {
                if (_identityValidator(doubleCheckedCached))
                {
                    using var hitTrace = PerfTrace.Stage("launch_resolution=hit");
                    _telemetry.IncLaunchResolutionHits();
                    var app = new AppService.ResolvedApp(
                        doubleCheckedCached.Method,
                        doubleCheckedCached.Identifier,
                        doubleCheckedCached.DisplayName,
                        doubleCheckedCached.ExeHints,
                        doubleCheckedCached.Score);
                    return (app, false, null);
                }
            }

            if (!_inFlightMisses.TryGetValue(key, out task!))
            {
                using (PerfTrace.Stage("launch_resolution=miss"))
                    _telemetry.IncLaunchResolutionMisses();

                task = Task.Run(async () =>
                {
                    try
                    {
                        var result = await resolver().ConfigureAwait(false);

                        // If not found (and not ambiguous), allow ONE bounded authoritative refresh/retry
                        if (!result.IsAmbiguous && result.Best == null)
                        {
                            await _catalogService.RefreshAsync().ConfigureAwait(false);
                            result = await resolver().ConfigureAwait(false);
                        }

                        // If ambiguous or not found, DO NOT cache a winner!
                        if (result.IsAmbiguous || result.Best == null)
                            return result;

                        // Unambiguous winner: cache it
                        var best = result.Best;
                        var appPathKey = best.Method == LaunchMethod.AppPath
                            ? (best.ExeHints.Length > 0 ? (best.ExeHints[0].EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? best.ExeHints[0] : best.ExeHints[0] + ".exe") : Path.GetFileName(best.Identifier))
                            : null;

                        var activeSnap = _catalogService.GetSnapshot();
                        var entry = new LaunchCacheEntryDto
                        {
                            NormalizedApp = key.NormalizedApp,
                            ExactOnly = key.ExactOnly,
                            NewInstance = key.NewInstance,
                            CanonicalArguments = key.CanonicalArguments,
                            ArgumentsDigest = key.ArgumentsDigest,
                            Method = best.Method,
                            Identifier = best.Identifier,
                            SourceKey = appPathKey,
                            DisplayName = best.DisplayName,
                            ExeHints = best.ExeHints,
                            Score = best.Score,
                            CatalogGeneration = activeSnap.Generation,
                            CatalogSnapshotId = activeSnap.SnapshotId,
                            ResolvedAtUtc = _clock()
                        };

                        _entries[key] = entry;
                        _telemetry.IncLaunchResolutionInserts();
                        _telemetry.SetLaunchResolutionEntryCount(_entries.Count);

                        // Persist to disk
                        SaveToDisk();

                        return result;
                    }
                    finally
                    {
                        lock (_inFlightLock)
                        {
                            _inFlightMisses.Remove(key);
                        }
                    }
                });

                _inFlightMisses[key] = task;
            }
        }

        var res = await task.ConfigureAwait(false);
        return (res.Best, res.IsAmbiguous, res.Candidates);
    }

    public Task<(AppService.ResolvedApp? Resolved, bool IsAmbiguous, List<AppService.ResolvedApp>? AmbiguousCandidates)> GetOrResolveAsync(
        string appName,
        bool exactOnly,
        bool newInstance,
        Func<Task<(AppService.ResolvedApp? Best, bool IsAmbiguous, List<AppService.ResolvedApp>? Candidates)>> resolver,
        IEnumerable<string>? arguments)
    {
        return GetOrResolveAsync(appName, exactOnly, newInstance, resolver, arguments != null ? string.Join(" ", arguments) : null);
    }

    /// <summary>
    /// Synchronous wrapper for GetOrResolveAsync.
    /// </summary>
    public (AppService.ResolvedApp? Resolved, bool IsAmbiguous, List<AppService.ResolvedApp>? AmbiguousCandidates) GetOrResolve(
        string appName,
        bool exactOnly,
        bool newInstance,
        Func<(AppService.ResolvedApp? Best, bool IsAmbiguous, List<AppService.ResolvedApp>? Candidates)> resolver,
        string? arguments = null)
    {
        return GetOrResolveAsync(appName, exactOnly, newInstance, () => Task.FromResult(resolver()), arguments).GetAwaiter().GetResult();
    }

    public (AppService.ResolvedApp? Resolved, bool IsAmbiguous, List<AppService.ResolvedApp>? AmbiguousCandidates) GetOrResolve(
        string appName,
        bool exactOnly,
        bool newInstance,
        Func<(AppService.ResolvedApp? Best, bool IsAmbiguous, List<AppService.ResolvedApp>? Candidates)> resolver,
        IEnumerable<string>? arguments)
    {
        return GetOrResolve(appName, exactOnly, newInstance, resolver, arguments != null ? string.Join(" ", arguments) : null);
    }

    private bool ValidateIdentity(LaunchCacheEntryDto entry)
    {
        try
        {
            switch (entry.Method)
            {
                case LaunchMethod.StartMenu:
                case LaunchMethod.Executable:
                case LaunchMethod.ExplicitPath:
                    return File.Exists(entry.Identifier);

                case LaunchMethod.AppPath:
                    var key = entry.SourceKey ?? (!string.IsNullOrEmpty(entry.DisplayName) ? (entry.DisplayName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? entry.DisplayName : entry.DisplayName + ".exe") : Path.GetFileName(entry.Identifier));
                    if (_appPathResolver != null)
                    {
                        var currentTarget = _appPathResolver(key);
                        if (string.IsNullOrEmpty(currentTarget))
                            return false; // key removed from registry
                        if (!string.Equals(currentTarget, entry.Identifier, StringComparison.OrdinalIgnoreCase))
                            return false; // target changed in registry!
                        return File.Exists(currentTarget);
                    }
                    return ValidateAppPathInRegistry(key, entry.Identifier);

                case LaunchMethod.Aumid:
                    if (string.IsNullOrEmpty(entry.Identifier) || !entry.Identifier.Contains('!'))
                        return false;

                    // If a custom package validator is provided, invoke it
                    if (_packageValidator != null)
                        return _packageValidator(entry.Identifier);

                    // Live package existence validation against OS package repository
                    return ValidateLivePackageInstalled(entry.Identifier);

                case LaunchMethod.Protocol:
                    return !string.IsNullOrEmpty(entry.Identifier);

                default:
                    return true;
            }
        }
        catch { return false; }
    }

    private static bool ValidateAppPathInRegistry(string appPathKey, string expectedTarget)
    {
        try
        {
            using var hkcu = Microsoft.Win32.Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\App Paths\{appPathKey}");
            var val = hkcu?.GetValue(null) as string;
            if (!string.IsNullOrEmpty(val))
            {
                var clean = val.Trim().Trim('"');
                if (!string.Equals(clean, expectedTarget, StringComparison.OrdinalIgnoreCase))
                    return false;
                return File.Exists(clean);
            }

            using var hklm = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\App Paths\{appPathKey}");
            val = hklm?.GetValue(null) as string;
            if (!string.IsNullOrEmpty(val))
            {
                var clean = val.Trim().Trim('"');
                if (!string.Equals(clean, expectedTarget, StringComparison.OrdinalIgnoreCase))
                    return false;
                return File.Exists(clean);
            }

            return false;
        }
        catch { return File.Exists(expectedTarget); }
    }

    private static bool ValidateLivePackageInstalled(string aumid)
    {
        try
        {
            var pfn = aumid.Split('!')[0];
            using var key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey($@"Extensions\ContractId\Windows.Launch\PackageId\{pfn}");
            if (key != null) return true;

            using var appKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
            if (appKey != null)
            {
                foreach (var sub in appKey.GetSubKeyNames())
                {
                    if (sub.StartsWith(pfn, StringComparison.OrdinalIgnoreCase) || pfn.StartsWith(sub.Split('_')[0], StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            return false;
        }
        catch { return false; }
    }

    public void InvalidateAll()
    {
        var count = _entries.Count;
        _entries.Clear();
        lock (_inFlightLock)
        {
            _inFlightMisses.Clear();
        }
        for (int i = 0; i < count; i++)
            _telemetry.IncLaunchResolutionInvalidations();
        _telemetry.SetLaunchResolutionEntryCount(0);
        SaveToDisk();
    }

    private void TryLoadFromDisk()
    {
        if (!File.Exists(_cacheFilePath))
            return;

        try
        {
            using var stream = new FileStream(_cacheFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var dto = JsonSerializer.Deserialize<LaunchCacheDto>(stream, JsonOptions);

            if (dto == null || dto.FormatVersion != ApplicationCatalogConstants.LaunchResolutionFormatVersion)
            {
                return;
            }

            foreach (var item in dto.Entries)
            {
                var key = new LaunchResolutionKey(item.NormalizedApp, item.ExactOnly, item.NewInstance, item.CanonicalArguments);
                _entries[key] = item;
            }
            _telemetry.SetLaunchResolutionEntryCount(_entries.Count);
        }
        catch { }
    }

    public void SaveToDisk()
    {
        try
        {
            Directory.CreateDirectory(_storageDir);
            var dto = new LaunchCacheDto
            {
                FormatVersion = ApplicationCatalogConstants.LaunchResolutionFormatVersion,
                CatalogGeneration = _catalogService.CurrentGeneration,
                CatalogSnapshotId = _catalogService.CurrentSnapshotId,
                SavedAtUtc = _clock(),
                Entries = _entries.Values.ToList()
            };

            var tempFilePath = Path.Combine(_storageDir, $"launch-cache.json.tmp.{Guid.NewGuid():N}");
            using (var stream = new FileStream(tempFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, dto, JsonOptions);
                stream.Flush(true);
            }

            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    File.Move(tempFilePath, _cacheFilePath, overwrite: true);
                    break;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(5);
                }
            }
        }
        catch { }
    }

    public void Dispose()
    {
        _catalogService.CatalogChanged -= OnCatalogChanged;
        _catalogService.CatalogSnapshotChanged -= OnCatalogSnapshotChanged;
        SaveToDisk();
    }
}
