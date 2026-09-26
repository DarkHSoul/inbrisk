namespace Inbrisk.Platform.Windows.Input;

/// <summary>A key press we must faithfully release — keeps enough state to
/// replay the UP exactly (unicode chars have WVk=0 and need their WScan).</summary>
public readonly record struct OwnedKey(ushort Vk, ushort Scan, uint Flags);

/// <summary>
/// RAII scope over a physical input sequence. Every DOWN the transaction
/// delivers is registered as owned; on Dispose — success, exception, timeout,
/// cancellation or emergency — owned input is released, verified, and any
/// residue is reconciled and reported as an invariant violation. No composite
/// operation may send DOWN events outside a transaction.
/// </summary>
public sealed class InputTransaction : IDisposable
{
    private static long _next;
    private readonly SendInputService _owner;
    private int _done;
    private int _abandoned; // watchdog force-released us

    internal InputTransaction(SendInputService owner, InputTxnKind kind, int budgetMs)
    {
        _owner = owner;
        Kind = kind;
        BudgetMs = budgetMs;
        Id = "t" + Interlocked.Increment(ref _next).ToString("x");
        StartedUtc = DateTimeOffset.UtcNow;
    }

    public string Id { get; }
    public InputTxnKind Kind { get; }
    public int BudgetMs { get; }
    public DateTimeOffset StartedUtc { get; }
    public bool Abandoned => Volatile.Read(ref _abandoned) != 0;

    internal readonly List<OwnedKey> OwnedKeys = new();
    internal readonly List<uint> OwnedButtons = new(); // DOWN flags

    internal void Abandon() => Interlocked.Exchange(ref _abandoned, 1);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _done, 1) != 0) return;
        _owner.EndTransaction(this);
    }
}
