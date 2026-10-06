using System.Diagnostics;

namespace Inbrisk.Mcp;

public sealed record PreLaunchSnapshot(
    DateTimeOffset SnapshotTime,
    HashSet<long> ExistingHwnds,
    Dictionary<int, DateTimeOffset?> ExistingPidsWithStartTime
);

public static class LaunchCreationEvidenceEvaluator
{
    public static bool EvaluateAgentOwnership(
        PreLaunchSnapshot? preSnapshot,
        long finalHwnd,
        int finalPid,
        DateTimeOffset? finalPidStartTime,
        int? spawnedPid,
        string? launchState,
        bool hasExplicitProcessSpawn = true,
        bool isVerifiedDescendantOrRedirect = false,
        bool platformProvidesLaunchIdentity = false,
        bool isHostedProcess = false)
    {
        // Rule 0: Safe default on invalid or ambiguous identifiers
        if (finalHwnd <= 0 || finalPid <= 0)
            return false;

        // Rule 1: Explicit AlreadyRunning or Reused state reported by launcher
        if (string.Equals(launchState, "AlreadyRunning", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(launchState, "Reused", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Rule 2: If no snapshot was captured, default to safe false
        if (preSnapshot == null)
            return false;

        // Rule 3: Pre-existing window reused
        if (preSnapshot.ExistingHwnds.Contains(finalHwnd))
            return false;

        // Rule 4: Bootstrap PID created, but final HWND belongs to a pre-existing PID.
        // Exception: isHostedProcess (e.g. ApplicationFrameHost) is a shared OS container process that naturally pre-exists.
        if (!isHostedProcess && preSnapshot.ExistingPidsWithStartTime.ContainsKey(finalPid))
            return false;

        // Rule 5: Process start time verification (checks against clock skew / PID reuse)
        if (!isHostedProcess)
        {
            if (finalPidStartTime.HasValue)
            {
                // If the process started before the pre-launch snapshot, it was running before this launch
                if (finalPidStartTime.Value < preSnapshot.SnapshotTime.AddMilliseconds(-100))
                    return false;
            }
            else
            {
                // Ambiguous creation evidence: start time could not be inspected
                // If we don't have an exact matching spawned PID, default to safe false
                if (!spawnedPid.HasValue || spawnedPid.Value <= 0 || spawnedPid.Value != finalPid)
                    return false;
            }
        }

        // Rule 6: POSITIVE CORRELATION MANDATE
        // A new PID + new HWND appearing post-snapshot is NOT sufficient by itself.
        // There must be positive, trustworthy correlation between the launch action and the final target:
        // Preference order:
        // 1. finalPid == process directly returned/spawned by this launch
        // 2. finalPid is an explicitly verified descendant / redirect target of the spawned process
        // 3. platform launch API provided explicit launch-specific identity/correlation
        // 4. isHostedProcess with new HWND created post-launch correlated by launcher
        bool hasPositiveCorrelation = false;

        if (spawnedPid.HasValue && spawnedPid.Value > 0 && finalPid == spawnedPid.Value)
        {
            hasPositiveCorrelation = true;
        }
        else if (isVerifiedDescendantOrRedirect)
        {
            hasPositiveCorrelation = true;
        }
        else if (platformProvidesLaunchIdentity || isHostedProcess)
        {
            hasPositiveCorrelation = true;
        }

        if (!hasPositiveCorrelation)
        {
            // Without verified positive correlation, SAFE DEFAULT is false!
            return false;
        }

        // All explicit creation evidence & positive correlation criteria verified
        return true;
    }
}
