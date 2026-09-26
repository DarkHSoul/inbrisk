using Inbrisk.Core;

namespace Inbrisk.Runtime;

/// <summary>
/// Deterministic model adapter for tests and demos. Each turn pops the next
/// scripted decision (a function that may inspect the real observation);
/// when the script runs out, Fallback is used (default: finish).
/// The Windows runtime underneath stays 100% real — only the "brain" is fake.
/// </summary>
public sealed class ScriptedModelAdapter : IModelAdapter
{
    private readonly Queue<Func<ModelTurnInput, ModelTurn>> _script;

    public ScriptedModelAdapter(IEnumerable<Func<ModelTurnInput, ModelTurn>> turns) =>
        _script = new Queue<Func<ModelTurnInput, ModelTurn>>(turns);

    /// <summary>Called when the script is exhausted.</summary>
    public Func<ModelTurnInput, ModelTurn>? Fallback { get; set; }

    /// <summary>Everything the adapter saw — for test introspection.</summary>
    public List<ModelTurnInput> Seen { get; } = new();

    public string Name => "scripted";

    public async Task<ModelTurn> DecideAsync(ModelTurnInput input, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // yield so the loop truly goes async — real providers await I/O here,
        // and a sync-completing fake would run the orchestrator on the caller's
        // thread (deadlocking pause/cancel tests)
        await Task.Yield();
        Seen.Add(input);
        var decide = _script.Count > 0 ? _script.Dequeue()
            : Fallback ?? (_ => new ModelTurn([new AgentAction(AgentActionKind.Finish,
                Result: "script exhausted")]));
        return decide(input);
    }

    /// <summary>Convenience: a turn that returns these actions verbatim.</summary>
    public static Func<ModelTurnInput, ModelTurn> Turn(params AgentAction[] actions) =>
        _ => new ModelTurn(actions);

    /// <summary>Convenience: find an element in the observation by name/role.</summary>
    public static string? FindId(ModelTurnInput i, string role, string? nameContains = null)
        => i.Observation.Elements.FirstOrDefault(e =>
            e.Role.Equals(role, StringComparison.OrdinalIgnoreCase)
            && (nameContains == null || e.Name?.Contains(nameContains,
                StringComparison.OrdinalIgnoreCase) == true))?.Id;
}
