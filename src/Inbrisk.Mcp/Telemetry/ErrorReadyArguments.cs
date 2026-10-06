using System;
using System.Collections.Generic;
using System.Linq;

namespace Inbrisk.Mcp.Telemetry;

/// <summary>
/// Structured error result enriched with deterministically corrected arguments
/// ready for immediate retry by the agent.
/// </summary>
public sealed record RetryReadyError
{
    public string Error { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public bool RetryReady { get; init; }
    public Dictionary<string, object?>? CorrectedArguments { get; init; }
    public string? SuggestedAction { get; init; }
    public IReadOnlyList<string>? DisambiguationCandidates { get; init; }
    public string Next { get; init; } = NextActionCodes.None;

    public Dictionary<string, object?> ToDictionary()
    {
        var dict = new Dictionary<string, object?>
        {
            ["ok"] = false,
            ["st"] = "Error",
            ["error"] = Error,
            ["message"] = Message,
            ["retryReady"] = RetryReady,
            ["next"] = Next
        };

        if (CorrectedArguments != null && CorrectedArguments.Count > 0)
        {
            dict["correctedArguments"] = CorrectedArguments;
            dict["suggestedRetry"] = CorrectedArguments;
        }

        if (!string.IsNullOrEmpty(SuggestedAction))
        {
            dict["suggestedAction"] = SuggestedAction;
        }

        if (DisambiguationCandidates != null && DisambiguationCandidates.Count > 0)
        {
            dict["disambiguationCandidates"] = DisambiguationCandidates;
        }

        return dict;
    }
}

/// <summary>
/// Enhances TargetNotFound, Ambiguous, and Malformed errors to supply
/// retry-ready corrected arguments where deterministically known.
/// </summary>
public static class ErrorReadyArguments
{
    public const string TargetNotFoundError = "TargetNotFound";
    public const string AmbiguousTargetError = "AmbiguousTarget";
    public const string MalformedArgumentsError = "MalformedArguments";

    /// <summary>
    /// Constructs a TargetNotFound error. If candidates contain a deterministic match (e.g. case-insensitive
    /// or close match), supplies corrected arguments for direct retry.
    /// </summary>
    public static RetryReadyError ForTargetNotFound(
        string failedTarget,
        IEnumerable<string>? availableCandidates = null,
        string? deterministicallyKnownMatch = null,
        IDictionary<string, object?>? originalArgs = null)
    {
        var candidatesList = availableCandidates?.ToList() ?? new List<string>();
        string? suggestedMatch = deterministicallyKnownMatch;

        if (string.IsNullOrEmpty(suggestedMatch) && candidatesList.Count > 0)
        {
            // 1. Exact case-insensitive match
            suggestedMatch = candidatesList.FirstOrDefault(c =>
                c.Equals(failedTarget, StringComparison.OrdinalIgnoreCase));

            // 2. Contains match
            if (string.IsNullOrEmpty(suggestedMatch))
            {
                suggestedMatch = candidatesList.FirstOrDefault(c =>
                    c.Contains(failedTarget, StringComparison.OrdinalIgnoreCase) ||
                    failedTarget.Contains(c, StringComparison.OrdinalIgnoreCase));
            }

            // 3. Single candidate in scope
            if (string.IsNullOrEmpty(suggestedMatch) && candidatesList.Count == 1)
            {
                suggestedMatch = candidatesList[0];
            }
        }

        Dictionary<string, object?>? corrected = null;
        bool retryReady = !string.IsNullOrEmpty(suggestedMatch);

        if (retryReady)
        {
            corrected = originalArgs != null
                ? new Dictionary<string, object?>(originalArgs, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            if (corrected.TryGetValue("target", out var targetObj) && targetObj is IDictionary<string, object?> targetDict)
            {
                var newTarget = new Dictionary<string, object?>(targetDict, StringComparer.OrdinalIgnoreCase)
                {
                    ["name"] = suggestedMatch
                };
                corrected["target"] = newTarget;
            }
            else
            {
                corrected["target"] = new Dictionary<string, object?> { ["name"] = suggestedMatch };
            }
        }

        var message = retryReady
            ? $"Target '{failedTarget}' was not found, but a deterministic match '{suggestedMatch}' was identified. Corrected arguments supplied for immediate retry."
            : $"Target '{failedTarget}' was not found and no deterministic match was found.";

        return new RetryReadyError
        {
            Error = TargetNotFoundError,
            Message = message,
            RetryReady = retryReady,
            CorrectedArguments = corrected,
            SuggestedAction = retryReady ? "retry_with_corrected_target" : "observe",
            DisambiguationCandidates = candidatesList.AsReadOnly(),
            Next = retryReady ? NextActionCodes.None : NextActionCodes.Observe
        };
    }

    /// <summary>
    /// Constructs an AmbiguousTarget error when multiple elements match the selector.
    /// Provides disambiguated corrected arguments using index: 0 or primary candidate elementId.
    /// </summary>
    public static RetryReadyError ForAmbiguous(
        string targetQuery,
        IEnumerable<string> matchingCandidates,
        string? resolvedPrimaryCandidate = null,
        IDictionary<string, object?>? originalArgs = null)
    {
        var candidatesList = matchingCandidates?.ToList() ?? new List<string>();
        var primary = resolvedPrimaryCandidate ?? candidatesList.FirstOrDefault();

        var corrected = originalArgs != null
            ? new Dictionary<string, object?>(originalArgs, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        bool retryReady = !string.IsNullOrEmpty(primary);
        if (retryReady)
        {
            corrected["elementId"] = primary;
            corrected["index"] = 0;
        }

        var message = $"Query '{targetQuery}' matched {candidatesList.Count} elements. Disambiguation required. Defaulted to primary candidate '{primary}' at index 0.";

        return new RetryReadyError
        {
            Error = AmbiguousTargetError,
            Message = message,
            RetryReady = retryReady,
            CorrectedArguments = retryReady ? corrected : null,
            SuggestedAction = "retry_with_disambiguated_target",
            DisambiguationCandidates = candidatesList.AsReadOnly(),
            Next = NextActionCodes.None
        };
    }

    /// <summary>
    /// Constructs a MalformedArguments error, supplying corrected arguments
    /// where deterministic syntax or structure corrections are known.
    /// </summary>
    public static RetryReadyError ForMalformed(
        string reason,
        IDictionary<string, object?>? invalidArgs,
        IDictionary<string, object?> correctedArgs,
        string? suggestedAction = null)
    {
        ArgumentNullException.ThrowIfNull(correctedArgs);

        return new RetryReadyError
        {
            Error = MalformedArgumentsError,
            Message = $"Malformed tool arguments: {reason}. Corrected arguments provided for retry.",
            RetryReady = true,
            CorrectedArguments = new Dictionary<string, object?>(correctedArgs, StringComparer.OrdinalIgnoreCase),
            SuggestedAction = suggestedAction ?? "retry_with_corrected_arguments",
            Next = NextActionCodes.None
        };
    }

    /// <summary>
    /// Generic enhancer that augments an error dictionary with retry-ready corrected arguments.
    /// </summary>
    public static Dictionary<string, object?> EnhanceError(
        string error,
        string message,
        IDictionary<string, object?>? originalArgs = null,
        IDictionary<string, object?>? correctedArgs = null,
        IEnumerable<string>? candidates = null)
    {
        bool retryReady = correctedArgs != null && correctedArgs.Count > 0;
        var candidatesList = candidates?.ToList();

        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ok"] = false,
            ["st"] = "Error",
            ["error"] = error,
            ["message"] = message,
            ["retryReady"] = retryReady,
            ["next"] = retryReady ? NextActionCodes.None : NextActionCodes.Observe
        };

        if (retryReady)
        {
            dict["correctedArguments"] = new Dictionary<string, object?>(correctedArgs!, StringComparer.OrdinalIgnoreCase);
            dict["suggestedRetry"] = dict["correctedArguments"];
        }

        if (candidatesList != null && candidatesList.Count > 0)
        {
            dict["disambiguationCandidates"] = candidatesList;
        }

        return dict;
    }
}
