using System;
using System.Threading;
using System.Threading.Tasks;
using Inbrisk.Core;

namespace Inbrisk.Platform.Windows.Apps;

public sealed record LandmarkHealingOutcome(
    bool Success,
    bool Evicted,
    bool Rediscovered,
    bool Validated,
    LandmarkKnowledge? Knowledge,
    string? Error = null);

/// <summary>
/// Handles deterministic self-healing of application landmarks.
/// Invariant:
/// If a cached landmark selector fails during resolution:
/// 1. Evicts ONLY the affected landmark from the profile (other landmarks remain untouched).
/// 2. Persists the eviction immediately so stale knowledge does not persist.
/// 3. Triggers live rediscovery to find an updated candidate selector.
/// 4. Validates the newly discovered landmark candidate against live UI verification rules.
/// 5. Updates and saves the profile ONLY IF the newly discovered landmark validates.
/// </summary>
public sealed class LandmarkSelfHealer
{
    private readonly ApplicationProfileStore _store;

    public LandmarkSelfHealer(ApplicationProfileStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>
    /// Attempts resolution with self-healing fallback.
    /// </summary>
    public async Task<LandmarkHealingOutcome> ResolveOrHealAsync(
        ApplicationProfile profile,
        string landmarkName,
        Func<LandmarkSelector, CancellationToken, Task<UiElement?>> resolver,
        Func<string, CancellationToken, Task<LandmarkKnowledge?>> rediscovery,
        Func<LandmarkKnowledge, UiElement?, CancellationToken, Task<bool>> validator,
        CancellationToken ct = default)
    {
        if (profile == null) throw new ArgumentNullException(nameof(profile));
        if (string.IsNullOrWhiteSpace(landmarkName)) throw new ArgumentException("Landmark name required", nameof(landmarkName));

        // 1. Check if cached landmark exists in profile
        if (profile.Landmarks.TryGetValue(landmarkName, out var cached))
        {
            try
            {
                var el = await resolver(cached.Selector, ct).ConfigureAwait(false);
                if (el != null)
                {
                    bool isValid = await validator(cached, el, ct).ConfigureAwait(false);
                    if (isValid)
                    {
                        cached.Validated = true;
                        cached.LastValidatedAtUtc = DateTimeOffset.UtcNow;
                        return new LandmarkHealingOutcome(
                            Success: true,
                            Evicted: false,
                            Rediscovered: false,
                            Validated: true,
                            Knowledge: cached);
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* Resolution failed -> proceed to eviction and self-healing */ }
        }

        // 2. Resolution failed! Evict ONLY the affected landmark from the profile.
        // Other landmarks and version-agnostic metadata remain completely intact.
        bool wasEvicted = profile.RemoveLandmark(landmarkName);
        if (wasEvicted)
        {
            try
            {
                _store.SaveProfile(profile);
            }
            catch { }
        }

        // 3. Trigger live rediscovery
        LandmarkKnowledge? candidate = null;
        try
        {
            candidate = await rediscovery(landmarkName, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* rediscovery error */ }

        if (candidate == null)
        {
            return new LandmarkHealingOutcome(
                Success: false,
                Evicted: wasEvicted,
                Rediscovered: false,
                Validated: false,
                Knowledge: null,
                Error: $"Cached landmark '{landmarkName}' failed and live rediscovery returned no candidate.");
        }

        // 4. Validate newly discovered candidate against live UI verification
        UiElement? candidateEl = null;
        bool candidateValidated = false;
        try
        {
            candidateEl = await resolver(candidate.Selector, ct).ConfigureAwait(false);
            if (candidateEl != null)
            {
                candidateValidated = await validator(candidate, candidateEl, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* validation error */ }

        // 5. Update profile ONLY IF newly discovered landmark validates
        if (candidateValidated)
        {
            candidate.Validated = true;
            candidate.LastValidatedAtUtc = DateTimeOffset.UtcNow;
            profile.AddOrUpdateLandmark(candidate);
            try
            {
                _store.SaveProfile(profile);
            }
            catch { }

            return new LandmarkHealingOutcome(
                Success: true,
                Evicted: wasEvicted,
                Rediscovered: true,
                Validated: true,
                Knowledge: candidate);
        }

        // Newly discovered landmark failed validation: profile is NOT updated with candidate.
        // Affected landmark remains evicted.
        return new LandmarkHealingOutcome(
            Success: false,
            Evicted: wasEvicted,
            Rediscovered: true,
            Validated: false,
            Knowledge: null,
            Error: $"Discovered candidate for '{landmarkName}' failed live validation.");
    }
}
