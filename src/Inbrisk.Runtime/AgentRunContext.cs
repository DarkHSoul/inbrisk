using System.Diagnostics;
using Inbrisk.Core;

namespace Inbrisk.Runtime;

/// <summary>
/// Everything a single agent run owns: identity, cancellation, pause gate,
/// confirmer, budget/policy config, deadline, and history. Two concurrent
/// runs never share this object — there is no process-global mutable run
/// state. The lifecycle state machine lives here too.
/// </summary>
public sealed class AgentRunContext : IDisposable
{
    private readonly object _gate = new();

    public string RunId { get; } = Guid.NewGuid().ToString("N")[..10];
    public CancellationTokenSource Cts { get; } = new();
    /// <summary>Reset while paused; all action starts wait on it.</summary>
    public ManualResetEventSlim PauseGate { get; } = new(initialState: true);
    public AgentOptions Options { get; }
    public Stopwatch Clock { get; } = Stopwatch.StartNew();
    /// <summary>Debug history — complete, never compacted.</summary>
    public List<StepRecord> History { get; } = new();
    /// <summary>Model-facing history — compacted under MaxHistoryEntries.</summary>
    public List<CondensedStep> Condensed { get; } = new();
    /// <summary>Deterministic summary of compacted older steps.</summary>
    public string? HistorySummary { get; set; }
    /// <summary>Element ids touched by recent actions — relevance signal.</summary>
    public List<string> RecentElementRefs { get; } = new();
    /// <summary>Set on Resume — the next observation must be rebuilt before
    /// any action/model call consumes the stale one.</summary>
    public volatile bool NeedsReobserve;
    /// <summary>Action currently being executed — lets the confirmer hook
    /// present the canonical action instead of a raw intent.</summary>
    public AgentAction? CurrentAction;

    private volatile AgentStatus _status = AgentStatus.Created;
    public AgentStatus Status => _status;
    public TimeSpan Deadline => TimeSpan.FromMilliseconds(Options.TimeoutMs);
    public CancellationToken Token => Cts.Token;

    public AgentRunContext(AgentOptions options) => Options = options;

    /// <summary>Guarded lifecycle transition. Illegal transitions return
    /// false — a Completed run cannot be resumed, a Created run cannot be
    /// paused, etc.</summary>
    public bool Transition(AgentStatus to)
    {
        lock (_gate)
        {
            var from = _status;
            var ok = (from, to) switch
            {
                (AgentStatus.Created, AgentStatus.Running) => true,
                (AgentStatus.Running, AgentStatus.Paused) => true,
                (AgentStatus.Paused, AgentStatus.Running) => true,
                (AgentStatus.Running or AgentStatus.Paused, AgentStatus.Cancelling) => true,
                (AgentStatus.Running or AgentStatus.Paused or AgentStatus.Cancelling,
                    AgentStatus.Cancelled) => true,
                (AgentStatus.Running or AgentStatus.Paused, AgentStatus.Completed) => true,
                (AgentStatus.Running or AgentStatus.Paused or AgentStatus.Cancelling,
                    AgentStatus.Failed) => true,
                (AgentStatus.Running or AgentStatus.Paused or AgentStatus.Cancelling,
                    AgentStatus.TimedOut) => true,
                (AgentStatus.Running or AgentStatus.Paused or AgentStatus.Cancelling,
                    AgentStatus.Blocked) => true,
                _ => false,
            };
            if (ok) _status = to;
            return ok;
        }
    }

    /// <summary>Run-scoped confirmer: presents the canonical action to the
    /// run's own Confirmer option; never mutates process-global policy.</summary>
    public bool Confirm(ActionIntent intent, UiElement? el, WindowInfo? win)
    {
        var c = Options.Confirmer;
        if (c == null) return false;
        var action = CurrentAction ?? MapBack(intent);
        return c(action, win?.Title ?? el?.Name);
    }

    public void NoteElementRef(string? elementId)
    {
        if (elementId == null) return;
        RecentElementRefs.Add(elementId);
        if (RecentElementRefs.Count > 8) RecentElementRefs.RemoveAt(0);
    }

    public void Dispose()
    {
        Cts.Dispose();
        PauseGate.Dispose();
    }

    /// <summary>Best-effort AgentAction reconstruction for the confirmer hook
    /// when the intent arrived from a non-agent path.</summary>
    private static AgentAction MapBack(ActionIntent i) => new(
        i.Kind switch
        {
            ActionKind.Click => AgentActionKind.Click,
            ActionKind.TypeText => AgentActionKind.Type,
            ActionKind.SetValue => AgentActionKind.SetValue,
            _ => AgentActionKind.Observe,
        });
}
