namespace Inbrisk.Runtime;

/// <summary>Lifecycle of a server-side confirm challenge (F04).</summary>
public enum ConfirmChallengeState { Pending, Approved, Denied, Consumed }

/// <summary>
/// F04: a server-issued approval grant for one CONFIRM-class action on one
/// specific target. The executor issues a challenge when confirmation is
/// required; only in-process code (a local approval surface — control
/// window / tray) can flip it to <see cref="ConfirmChallengeState.Approved"/>
/// via <see cref="SafetyPolicy.ApproveConfirmation"/>. The client retries
/// with the approval id, which binds to the action+target fingerprint,
/// expires, and is consumed on first use — the tool call itself can never
/// mint one.
/// </summary>
public sealed class ConfirmChallenge
{
    internal ConfirmChallenge(string approvalId, string actionHash,
        string description, DateTimeOffset expiresAt)
    {
        ApprovalId = approvalId;
        ActionHash = actionHash;
        Description = description;
        ExpiresAt = expiresAt;
    }

    /// <summary>Server-issued id the retry must echo back via args.approvalId.</summary>
    public string ApprovalId { get; }

    /// <summary>Fingerprint of kind + resolved target + args — the grant
    /// is valid only for the action it was issued for.</summary>
    public string ActionHash { get; }

    /// <summary>Human-readable summary for a local approval UI.</summary>
    public string Description { get; }

    public DateTimeOffset ExpiresAt { get; }

    public ConfirmChallengeState State { get; internal set; } = ConfirmChallengeState.Pending;

    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;
}
