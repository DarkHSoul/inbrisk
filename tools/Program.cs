// M3 e2e demo: scripted adapter drives the real TestApp through the
// canonical action protocol — every step printed live.
using Inbrisk.Core;
using Inbrisk.Runtime;
using Inbrisk.Sdk;

var rt = new InbriskRuntime(new InbriskOptions(AutoConfirm: true, StartEvents: true));
var win = rt.Windows().FirstOrDefault(w => w.ProcessName?.StartsWith("Inbrisk.TestApp") == true)
    ?? throw new Exception("start TestApp first");
File.WriteAllText(Path.Combine(Path.GetTempPath(), "inbrisk_testapp", "state.txt"), "idle");

AgentAction A(AgentActionKind k, string? el = null, string? text = null,
    string? result = null) => new(k, ElementId: el, Text: text, Result: result);

var adapter = new ScriptedModelAdapter(new Func<ModelTurnInput, ModelTurn>[]
{
    i =>
    {
        var obs = i.Observation;
        Console.WriteLine($"[obs#{obs.ObservationId}] active='{obs.ActiveWindow?.Title}' " +
            $"elements={obs.Elements.Count} frames={obs.Frames.Count} " +
            $"events={obs.RecentEvents.Count} backend={obs.CaptureBackend}");
        return new ModelTurn([A(AgentActionKind.SetValue,
            ScriptedModelAdapter.FindId(i, "Edit", "Main text"), "m3-demo")]);
    },
    i =>
    {
        var d = i.Observation.Delta;
        Console.WriteLine($"[delta] {string.Join(" | ", d?.Notes ?? (IReadOnlyList<string>)[])}");
        return new ModelTurn([A(AgentActionKind.Toggle,
            ScriptedModelAdapter.FindId(i, "CheckBox", "Enable"))]);
    },
    i => new ModelTurn([A(AgentActionKind.Invoke,
        ScriptedModelAdapter.FindId(i, "Button", "Save"))]),
    _ => new ModelTurn([A(AgentActionKind.Finish, result: "demo complete")]),
});

var agent = rt.CreateAgent(new AgentOptions(MaxSteps: 12));
agent.StepCompleted += s =>
    Console.WriteLine($"  step{s.Step} {s.Action.Summary(),-42} → {s.Outcome.Kind}" +
        (s.Outcome.Method != null ? $" via {s.Outcome.Method}" : "") +
        (s.Outcome.Evidence != null ? $"  evidence: {s.Outcome.Evidence}" : "") +
        (s.Outcome.Detail != null ? $" ({s.Outcome.Detail})" : "") +
        (s.DeltaSummary is { Length: > 0 } ds ? $"  Δ: {ds}" : ""));

var r = agent.Run("fill the form and save it", adapter, win.Hwnd);
Console.WriteLine($"\n[result] {r.Status} steps={r.Steps} final='{r.FinalResult}' reason={r.Reason}");

var stateFile = Path.Combine(Path.GetTempPath(), "inbrisk_testapp", "state.txt");
using (var fs = new FileStream(stateFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
using (var sr = new StreamReader(fs))
    Console.WriteLine($"[state] {sr.ReadToEnd()}");
rt.Dispose();
