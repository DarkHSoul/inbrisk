namespace Inbrisk.Core;

/// <summary>
/// Reference-holder ambient context tracking lock hierarchy state across async methods and continuations.
/// Copy-on-write branch semantics prevent sibling async branch contamination while preserving child scope inheritance.
/// </summary>
public sealed class LockContext
{
    public int HeldProcessBarriers { get; set; }

    /// <summary>Pids whose Rank 2 process write barrier is held by this
    /// context (or an ancestor context it was branched from). The per-pid
    /// WriteGate semaphore is NOT reentrant — this set lets the scheduler
    /// detect same-context re-entry (e.g. an action chain that holds the
    /// barrier for the whole chain while each step's PerformNative
    /// re-notifies the same pid) and turn it into a no-op instead of a
    /// self-deadlock against WriteGateTimeoutMs. Reads scheduled by a
    /// holding context also bypass the write block via this set — the
    /// holder would otherwise deadlock against its own barrier.</summary>
    public HashSet<int> HeldBarrierPids { get; } = new();

    /// <summary>Depth of reentrant NotifyMutationStarting calls per pid —
    /// the paired NotifyMutationCompleted decrements instead of releasing
    /// an ancestor-held gate.</summary>
    public Dictionary<int, int>? ReentrantNotifyDepth { get; set; }
}

/// <summary>
/// Enforces canonical concurrency hierarchy across Inbrisk async contexts:
/// Rank 1 (Global Desktop Gate: DesktopArbiter) -> Rank 2 (Process Barrier: UiaReadScheduler).
/// While Rank 2 is held on the current async context, acquiring Rank 1 is strictly forbidden (zero Rank 2 -> Rank 1 inversion).
/// </summary>
public static class LockOrderTracker
{
    private static readonly AsyncLocal<LockContext?> _currentContext = new();

    public static LockContext CurrentContext
    {
        get
        {
            var ctx = _currentContext.Value;
            if (ctx == null)
            {
                ctx = new LockContext();
                _currentContext.Value = ctx;
            }
            return ctx;
        }
        set => _currentContext.Value = value;
    }

    public static int CurrentHeldProcessBarriers => _currentContext.Value?.HeldProcessBarriers ?? 0;

    public static void AssertCanAcquireRank1()
    {
        if (CurrentHeldProcessBarriers > 0)
        {
            throw new InvalidOperationException("Lock order violation: cannot acquire Rank 1 (DesktopArbiter) while holding Rank 2 (Process Barrier)!");
        }
    }
}
