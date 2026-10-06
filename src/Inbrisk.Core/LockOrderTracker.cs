namespace Inbrisk.Core;

/// <summary>
/// Reference-holder ambient context tracking lock hierarchy state across async methods and continuations.
/// Copy-on-write branch semantics prevent sibling async branch contamination while preserving child scope inheritance.
/// </summary>
public sealed class LockContext
{
    public int HeldProcessBarriers { get; set; }
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
