using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Inbrisk.Core;

namespace Inbrisk.Platform.Windows.Apps;

public static class ApplicationCatalogConstants
{
    public const int PersistentCatalogFormatVersion = 1;
    public const int LaunchResolutionFormatVersion = 1;
    public static readonly TimeSpan SafetyTtl = TimeSpan.FromHours(24);
}

/// <summary>
/// Root persistent DTO for the disk-persisted Application Catalog.
/// </summary>
public sealed class AppCatalogDto
{
    public int FormatVersion { get; set; } = ApplicationCatalogConstants.PersistentCatalogFormatVersion;
    public string SnapshotId { get; set; } = Guid.NewGuid().ToString("N");
    public long Generation { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? SourceTimestampUtc { get; set; }
    public List<AppCatalogEntryDto> Entries { get; set; } = new();
    public string? Fingerprint { get; set; }
}

/// <summary>
/// Entry stored in the persistent Application Catalog.
/// Plain data DTO: No handles, COM pointers, or transient OS state.
/// </summary>
public sealed class AppCatalogEntryDto
{
    public string Name { get; set; } = "";
    public LaunchMethod Method { get; set; }
    public string Launch { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? TargetStem { get; set; }
    public string[] ExeHints { get; set; } = Array.Empty<string>();
    public int Score { get; set; }
    public string Identifier { get; set; } = "";
}

/// <summary>
/// Immutable snapshot of the in-memory application catalog, atomically swapped on refresh.
/// </summary>
public sealed class ApplicationCatalogSnapshot
{
    public IReadOnlyList<AppInfo> Apps { get; }
    public IReadOnlyList<AppCatalogEntryDto> Entries { get; }
    public long Generation { get; }
    public long Revision { get; }
    public string SnapshotId { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset? SourceTimestampUtc { get; }
    public string? Fingerprint { get; }

    public ApplicationCatalogSnapshot(
        IReadOnlyList<AppInfo> apps,
        IReadOnlyList<AppCatalogEntryDto> entries,
        long generation,
        DateTimeOffset createdAtUtc,
        string? snapshotId = null,
        DateTimeOffset? sourceTimestampUtc = null,
        string? fingerprint = null,
        long revision = 1)
    {
        Apps = apps;
        Entries = entries;
        Generation = generation;
        Revision = revision;
        CreatedAtUtc = createdAtUtc;
        SnapshotId = string.IsNullOrEmpty(snapshotId) ? Guid.NewGuid().ToString("N") : snapshotId;
        SourceTimestampUtc = sourceTimestampUtc;
        Fingerprint = fingerprint;
    }
}

/// <summary>
/// Structured, collision-safe key for launch resolution caching.
/// Enforces canonical argument normalization and deterministic SHA-256 digesting
/// to guarantee cross-process and restart determinism without relying on randomized runtime hashes.
/// </summary>
public sealed class LaunchResolutionKey : IEquatable<LaunchResolutionKey>
{
    public string NormalizedApp { get; }
    public bool ExactOnly { get; }
    public bool NewInstance { get; }
    public string CanonicalArguments { get; }
    public string ArgumentsDigest { get; }

    public LaunchResolutionKey(string app, bool exactOnly = false, bool newInstance = false, string? arguments = null)
    {
        NormalizedApp = (app ?? "").Trim().ToLowerInvariant();
        ExactOnly = exactOnly;
        NewInstance = newInstance;
        CanonicalArguments = (arguments ?? "").Trim();

        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(CanonicalArguments);
        var hash = sha.ComputeHash(bytes);
        ArgumentsDigest = Convert.ToHexString(hash).ToLowerInvariant();
    }

    public LaunchResolutionKey(string app, bool exactOnly, bool newInstance, IEnumerable<string>? arguments)
        : this(app, exactOnly, newInstance, arguments != null ? string.Join(" ", arguments) : null)
    {
    }

    public bool Equals(LaunchResolutionKey? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return string.Equals(NormalizedApp, other.NormalizedApp, StringComparison.Ordinal)
            && ExactOnly == other.ExactOnly
            && NewInstance == other.NewInstance
            && string.Equals(CanonicalArguments, other.CanonicalArguments, StringComparison.Ordinal);
    }

    public override bool Equals(object? obj) => Equals(obj as LaunchResolutionKey);

    public override int GetHashCode()
    {
        return HashCode.Combine(
            StringComparer.Ordinal.GetHashCode(NormalizedApp),
            ExactOnly,
            NewInstance,
            StringComparer.Ordinal.GetHashCode(CanonicalArguments));
    }

    public override string ToString() => $"{NormalizedApp}|args={ArgumentsDigest}|exact={ExactOnly}|new={NewInstance}";
}

/// <summary>
/// Root persistent DTO for the disk-persisted Launch-Resolution Cache.
/// </summary>
public sealed class LaunchCacheDto
{
    public int FormatVersion { get; set; } = ApplicationCatalogConstants.LaunchResolutionFormatVersion;
    public long CatalogGeneration { get; set; }
    public string CatalogSnapshotId { get; set; } = "";
    public DateTimeOffset SavedAtUtc { get; set; }
    public List<LaunchCacheEntryDto> Entries { get; set; } = new();
}

/// <summary>
/// Persistent entry for a cached launch resolution.
/// </summary>
public sealed class LaunchCacheEntryDto
{
    public string NormalizedApp { get; set; } = "";
    public bool ExactOnly { get; set; }
    public bool NewInstance { get; set; }
    public string CanonicalArguments { get; set; } = "";
    public string ArgumentsDigest { get; set; } = "";
    public LaunchMethod Method { get; set; }
    public string Identifier { get; set; } = "";
    public string? SourceKey { get; set; }
    public string DisplayName { get; set; } = "";
    public string[] ExeHints { get; set; } = Array.Empty<string>();
    public int Score { get; set; }
    public long CatalogGeneration { get; set; }
    public string CatalogSnapshotId { get; set; } = "";
    public DateTimeOffset ResolvedAtUtc { get; set; }
}
