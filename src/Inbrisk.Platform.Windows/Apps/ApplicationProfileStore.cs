using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Inbrisk.Core;

namespace Inbrisk.Platform.Windows.Apps;

/// <summary>
/// Exception thrown when a concurrent or delayed writer attempts to commit
/// a profile with an older revision than the validated knowledge already persisted on disk.
/// </summary>
public sealed class StaleProfileException : InvalidOperationException
{
    public StaleProfileException(string message) : base(message) { }
}

/// <summary>
/// Stable identity representation for an application.
/// Architectural invariant: Keyed ONLY by canonical executable path, package family, or app ID.
/// NEVER keyed by transient OS handles such as PID or HWND.
/// </summary>
public sealed class ApplicationIdentity : IEquatable<ApplicationIdentity>
{
    public string? CanonicalExecutablePath { get; }
    public string? PackageFamilyName { get; }
    public string? AppId { get; }
    public string DisplayName { get; }

    public ApplicationIdentity(
        string? canonicalExecutablePath = null,
        string? packageFamilyName = null,
        string? appId = null,
        string? displayName = null)
    {
        AssertNoTransientIdentity(canonicalExecutablePath, packageFamilyName, appId);

        CanonicalExecutablePath = !string.IsNullOrWhiteSpace(canonicalExecutablePath)
            ? NormalizeCanonicalPath(canonicalExecutablePath)
            : null;
        PackageFamilyName = !string.IsNullOrWhiteSpace(packageFamilyName)
            ? packageFamilyName.Trim()
            : null;
        AppId = !string.IsNullOrWhiteSpace(appId)
            ? appId.Trim()
            : null;
        DisplayName = displayName?.Trim() ?? "";

        if (CanonicalExecutablePath == null && PackageFamilyName == null && AppId == null)
        {
            throw new ArgumentException("At least one stable identity component (canonicalExecutablePath, packageFamilyName, appId) is required.");
        }
    }

    private static void AssertNoTransientIdentity(string? exe, string? pkg, string? app)
    {
        foreach (var val in new[] { exe, pkg, app })
        {
            if (string.IsNullOrWhiteSpace(val)) continue;
            var lower = val.Trim().ToLowerInvariant();
            if (lower.StartsWith("hwnd:") || lower.StartsWith("pid:") || Regex.IsMatch(lower, @"^0x[0-9a-f]+$") || Regex.IsMatch(lower, @"^\d+$"))
            {
                throw new ArgumentException($"Transient handle or PID '{val}' cannot be used as an application identity. Stable identity required.");
            }
        }
    }

    private static string NormalizeCanonicalPath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path.Trim());
            return full.ToLowerInvariant();
        }
        catch
        {
            return path.Trim().ToLowerInvariant();
        }
    }

    public string ToStableKey()
    {
        if (!string.IsNullOrEmpty(PackageFamilyName))
        {
            var baseKey = SanitizeFilename(PackageFamilyName);
            return string.IsNullOrEmpty(AppId) ? $"pkg_{baseKey}" : $"pkg_{baseKey}_{SanitizeFilename(AppId)}";
        }
        if (!string.IsNullOrEmpty(CanonicalExecutablePath))
        {
            var fileName = Path.GetFileName(CanonicalExecutablePath);
            var hash = ComputeSha256Hex(CanonicalExecutablePath);
            return $"exe_{SanitizeFilename(fileName)}_{hash[..8]}";
        }
        return $"app_{SanitizeFilename(AppId!)}";
    }

    private static string SanitizeFilename(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(invalid.Contains(c) || c is ':' or '/' or '\\' or ' ' ? '_' : c);
        }
        return sb.ToString();
    }

    private static string ComputeSha256Hex(string input)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public bool Equals(ApplicationIdentity? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return string.Equals(CanonicalExecutablePath, other.CanonicalExecutablePath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(PackageFamilyName, other.PackageFamilyName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(AppId, other.AppId, StringComparison.OrdinalIgnoreCase);
    }

    public override bool Equals(object? obj) => Equals(obj as ApplicationIdentity);

    public override int GetHashCode()
    {
        return HashCode.Combine(
            CanonicalExecutablePath?.ToLowerInvariant(),
            PackageFamilyName?.ToLowerInvariant(),
            AppId?.ToLowerInvariant());
    }

    public override string ToString() => $"AppIdentity({ToStableKey()}, Name={DisplayName})";
}

/// <summary>
/// Static, deterministic landmark selector.
/// Invariant: Persists ONLY static role, automationId, name, className, or process specifications.
/// NEVER contains live HWND, live COM pointers, live pixel bounds, or live element values.
/// </summary>
public sealed class LandmarkSelector
{
    public string? Role { get; set; }
    public string? Name { get; set; }
    public string? AutomationId { get; set; }
    public string? ClassName { get; set; }
    public string? NameContains { get; set; }
    public string? Process { get; set; }

    public void AssertNoLiveUiState()
    {
        foreach (var prop in new[] { Name, AutomationId, ClassName, NameContains, Process })
        {
            if (string.IsNullOrWhiteSpace(prop)) continue;
            var lower = prop.Trim().ToLowerInvariant();
            if (lower.StartsWith("uia_") || lower.StartsWith("hwnd:") || lower.StartsWith("0x"))
            {
                throw new InvalidOperationException($"Live UI state, transient element IDs, or HWNDs ('{prop}') are prohibited in landmark selectors.");
            }
        }
    }
}

/// <summary>
/// Validated landmark knowledge stored inside an application profile.
/// Versioning annotations allow automatic invalidation when an app version upgrade changes UI structure.
/// </summary>
public sealed class LandmarkKnowledge
{
    public string Name { get; set; } = "";
    public LandmarkSelector Selector { get; set; } = new();
    public string? TargetAppVersion { get; set; }
    public string? MinAppVersion { get; set; }
    public string? MaxAppVersion { get; set; }
    public bool Validated { get; set; }
    public DateTimeOffset? LastValidatedAtUtc { get; set; }
    public double Confidence { get; set; } = 1.0;
}

/// <summary>
/// Static hints for launching the application safely without arbitrary shell escalation.
/// </summary>
public sealed class LaunchHints
{
    public string[]? DefaultArguments { get; set; }
    public int? StartupDelayMs { get; set; }
    public LaunchMethod? PreferredLaunchMethod { get; set; }
}

/// <summary>
/// Root persistent profile for an application.
/// Strictly static knowledge: landmark selectors, recipes, constrained deep links, launch hints.
/// Versioned JSON format on disk with atomic write / swap semantics and monotonic revision ordering.
/// </summary>
public sealed class ApplicationProfile
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public ApplicationIdentity Identity { get; set; } = null!;
    public string? AppVersion { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }

    public Dictionary<string, LandmarkKnowledge> Landmarks { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<TaskRecipeDefinition> Recipes { get; set; } = new();
    public List<string> DeepLinks { get; set; } = new();
    public LaunchHints? Hints { get; set; }

    public void AddOrUpdateLandmark(LandmarkKnowledge landmark)
    {
        landmark.Selector.AssertNoLiveUiState();
        Landmarks[landmark.Name] = landmark;
    }

    public bool RemoveLandmark(string name)
    {
        return Landmarks.Remove(name);
    }

    public void AddRecipe(TaskRecipeDefinition recipe, ITaskRecipeStore? recipeStore = null)
    {
        // Enforce deduplicated storage by name
        Recipes.RemoveAll(r => string.Equals(r.Name, recipe.Name, StringComparison.OrdinalIgnoreCase));
        Recipes.Add(recipe);
        recipeStore?.Save(recipe);
    }

    public void AddDeepLink(string uri)
    {
        DeepLinkSecurity.AssertSafeDeepLink(uri);
        if (!DeepLinks.Contains(uri, StringComparer.OrdinalIgnoreCase))
        {
            DeepLinks.Add(uri);
        }
    }
}

/// <summary>
/// Security guardrails for URI deep links.
/// Constrains deep links strictly to supported application protocols.
/// Prevents arbitrary shell command execution, process spawning, and command line injection.
/// </summary>
public static class DeepLinkSecurity
{
    private static readonly HashSet<string> DisallowedSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "powershell", "pwsh", "bash", "sh", "wscript", "cscript",
        "ms-msdt", "search-ms", "file", "javascript", "vbscript", "shell",
        "ms-appinstaller", "conhost", "rundll32", "regsvr32"
    };

    private static readonly string[] DangerousExtensions =
    [
        ".exe", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".hta", ".scr", ".pif", ".dll", ".com", ".cpl"
    ];

    public static bool IsSafeDeepLink(string uri, out string? reason)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            reason = "URI cannot be empty";
            return false;
        }

        uri = uri.Trim();

        // Check for shell command line injection characters
        if (uri.IndexOfAny(['\0', '\r', '\n', '&', '|', ';', '`', '<', '>', '"']) >= 0)
        {
            reason = "URI contains disallowed shell metacharacters";
            return false;
        }

        // Scheme validation
        var colonIdx = uri.IndexOf(':');
        if (colonIdx <= 0)
        {
            reason = "URI must contain a valid scheme separator ':'";
            return false;
        }

        var scheme = uri[..colonIdx];
        if (DisallowedSchemes.Contains(scheme))
        {
            reason = $"Scheme '{scheme}' is forbidden to prevent arbitrary command execution";
            return false;
        }

        if (!scheme.All(c => char.IsLetterOrDigit(c) || c is '+' or '-' or '.'))
        {
            reason = $"Invalid scheme format in '{scheme}'";
            return false;
        }

        // Block executable file references inside the URI
        foreach (var ext in DangerousExtensions)
        {
            if (uri.Contains(ext, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"URI references dangerous executable extension '{ext}'";
                return false;
            }
        }

        reason = null;
        return true;
    }

    public static void AssertSafeDeepLink(string uri)
    {
        if (!IsSafeDeepLink(uri, out var reason))
        {
            throw new SecurityException($"Arbitrary command execution via deep link blocked: {reason}");
        }
    }
}

/// <summary>
/// Persistent store for application profiles on disk.
/// Enforces:
/// 1. Stable application identity keys (no PID/HWND).
/// 2. Atomic write / swap semantics (temp file write + atomic move, zero corruption under concurrent writers).
/// 3. Monotonic revision ordering and stale writer overwrite rejection.
/// 4. App version mismatch selector invalidation while preserving version-agnostic metadata.
/// 5. Absolute prohibition of live UI state (HWND, COM objects, current bounds, live values).
/// </summary>
public sealed class ApplicationProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _storageDir;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ITaskRecipeStore? _recipeStore;

    public string StorageDirectory => _storageDir;

    public ApplicationProfileStore(
        string? storageDirectory = null,
        Func<DateTimeOffset>? clock = null,
        ITaskRecipeStore? recipeStore = null)
    {
        _storageDir = storageDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "inbrisk", "profiles");
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _recipeStore = recipeStore;

        try
        {
            Directory.CreateDirectory(_storageDir);
        }
        catch { }
    }

    public string GetProfileFilePath(ApplicationIdentity identity)
    {
        return Path.Combine(_storageDir, $"{identity.ToStableKey()}.json");
    }

    public string GetLockFilePath(ApplicationIdentity identity)
    {
        return Path.Combine(_storageDir, $"{identity.ToStableKey()}.lock");
    }

    public ApplicationProfile? GetProfile(ApplicationIdentity identity, string? currentAppVersion = null)
    {
        var path = GetProfileFilePath(identity);
        if (!File.Exists(path))
            return null;

        ApplicationProfile? profile = null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            profile = JsonSerializer.Deserialize<ApplicationProfile>(stream, JsonOptions);
        }
        catch
        {
            return null;
        }

        if (profile == null)
            return null;

        // If current app version is provided and differs from stored version,
        // automatically prune incompatible selectors while preserving version-agnostic knowledge.
        if (!string.IsNullOrEmpty(currentAppVersion) &&
            !string.Equals(profile.AppVersion, currentAppVersion, StringComparison.OrdinalIgnoreCase))
        {
            bool mutated = InvalidateIncompatibleSelectors(profile, currentAppVersion);
            if (mutated)
            {
                SaveProfile(profile);
            }
        }

        return profile;
    }

    /// <summary>
    /// Invalidates selectors tied to a specific older or incompatible app version,
    /// while preserving version-agnostic selectors and metadata (recipes, hints, deep links).
    /// </summary>
    public static bool InvalidateIncompatibleSelectors(ApplicationProfile profile, string newAppVersion)
    {
        bool changed = false;
        var toRemove = new List<string>();

        foreach (var kvp in profile.Landmarks)
        {
            var lm = kvp.Value;
            if (!string.IsNullOrEmpty(lm.TargetAppVersion) &&
                !string.Equals(lm.TargetAppVersion, newAppVersion, StringComparison.OrdinalIgnoreCase))
            {
                toRemove.Add(kvp.Key);
            }
        }

        foreach (var name in toRemove)
        {
            profile.Landmarks.Remove(name);
            changed = true;
        }

        if (!string.Equals(profile.AppVersion, newAppVersion, StringComparison.OrdinalIgnoreCase))
        {
            profile.AppVersion = newAppVersion;
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Persists an application profile using atomic write/swap semantics and cross-process file locks.
    /// Rejects stale writers attempting to overwrite newer validated knowledge.
    /// Strictly verifies that no live UI state is persisted.
    /// </summary>
    public void SaveProfile(ApplicationProfile profile)
    {
        AssertNoLiveUiState(profile);

        var path = GetProfileFilePath(profile.Identity);
        var lockPath = GetLockFilePath(profile.Identity);
        var now = _clock();

        profile.UpdatedAtUtc = now;
        if (profile.CreatedAtUtc == default)
            profile.CreatedAtUtc = now;

        // Cross-process file lock for the specific profile
        using var lockStream = AcquireLock(lockPath);

        // Check for stale writer against current on-disk state
        if (File.Exists(path))
        {
            try
            {
                using var readStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var onDisk = JsonSerializer.Deserialize<ApplicationProfile>(readStream, JsonOptions);
                if (onDisk != null)
                {
                    // Invariant: Stale writer cannot overwrite newer validated knowledge!
                    if (onDisk.Revision > profile.Revision)
                    {
                        throw new StaleProfileException(
                            $"Stale writer detected. On-disk profile has revision {onDisk.Revision}, incoming profile has revision {profile.Revision}. Overwrite rejected.");
                    }
                    // Monotonic revision advancement
                    profile.Revision = Math.Max(profile.Revision, onDisk.Revision + 1);
                }
            }
            catch (StaleProfileException)
            {
                throw;
            }
            catch
            {
                // Proceed if existing file was corrupt or empty
            }
        }
        else
        {
            if (profile.Revision <= 0)
                profile.Revision = 1;
        }

        // Temp write and atomic swap
        var tempPath = Path.Combine(_storageDir, $"{profile.Identity.ToStableKey()}.json.tmp.{Guid.NewGuid():N}");
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, profile, JsonOptions);
                stream.Flush(true);
            }

            // Atomic file replace
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    File.Move(tempPath, path, overwrite: true);
                    break;
                }
                catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 9)
                {
                    Thread.Sleep(5 * (attempt + 1));
                }
            }

            // Sync recipes to existing recipe system if available
            if (_recipeStore != null)
            {
                foreach (var recipe in profile.Recipes)
                {
                    _recipeStore.Save(recipe);
                }
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }

    private FileStream AcquireLock(string lockPath, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                Thread.Sleep(10);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(10);
            }
        }
        throw new TimeoutException($"Timed out acquiring lock on {lockPath}");
    }

    /// <summary>
    /// Enforces the strict invariant that live UI state (HWND, COM objects, current bounds, live values)
    /// is NEVER persisted into persistent application profiles.
    /// </summary>
    public static void AssertNoLiveUiState(ApplicationProfile profile)
    {
        if (profile == null) throw new ArgumentNullException(nameof(profile));

        // Check identity
        if (profile.Identity == null)
            throw new InvalidOperationException("Profile identity is missing.");

        // Check landmarks
        foreach (var lm in profile.Landmarks.Values)
        {
            if (lm.Selector == null)
                throw new InvalidOperationException($"Landmark '{lm.Name}' has a null selector.");
            lm.Selector.AssertNoLiveUiState();
        }

        // Structural reflection check: assert no properties of type IntPtr, UIntPtr, COM, or RectPx exist
        var profileType = typeof(ApplicationProfile);
        foreach (var prop in profileType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var pType = prop.PropertyType;
            if (pType == typeof(IntPtr) || pType == typeof(UIntPtr) || pType == typeof(nint) || pType == typeof(nuint))
            {
                throw new InvalidOperationException($"Live handle type '{pType.Name}' found on property '{prop.Name}'. Live UI state is prohibited.");
            }
            if (string.Equals(prop.Name, "Hwnd", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(prop.Name, "Pid", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(prop.Name, "Bounds", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(prop.Name, "ComObject", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Live UI state property '{prop.Name}' found in ApplicationProfile.");
            }
        }
    }
}
