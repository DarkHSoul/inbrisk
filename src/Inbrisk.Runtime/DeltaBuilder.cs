using Inbrisk.Core;

namespace Inbrisk.Runtime;

/// <summary>
/// Computes the delta between two observations so the model gets
/// precise added/removed/changed/offscreen/budget signals instead of tree churn.
/// Elements match by StableKey (hierarchy + role + aid/name).
/// </summary>
public static class DeltaBuilder
{
    public static ObsDelta Compute(AgentObservation prev, AgentObservation cur,
        IReadOnlyList<UiElement>? rawCurrent = null)
    {
        var prevByKey = prev.Elements.GroupBy(e => e.StableKey)
            .ToDictionary(g => g.Key, g => g.First());
        var curByKey = cur.Elements.GroupBy(e => e.StableKey)
            .ToDictionary(g => g.Key, g => g.First());

        var added = new List<ObsElement>();
        var removed = new List<ObsElement>();
        var changed = new List<ObsElement>();
        var offscreen = new List<ObsElement>();
        var prunedByBudget = new List<ObsElement>();

        Dictionary<string, UiElement>? rawByKey = null;
        if (rawCurrent != null)
        {
            rawByKey = rawCurrent
                .Select(e => (El: e, Key: ObservationBuilder.StableKey(e, e.Handle?.Recipe?.AutomationId)))
                .GroupBy(x => x.Key)
                .ToDictionary(g => g.Key, g => g.First().El);
        }

        foreach (var (k, e) in curByKey)
        {
            if (!prevByKey.TryGetValue(k, out var p)) { added.Add(e); continue; }
            if (p.Name != e.Name || p.Value != e.Value || p.State != e.State
                || !p.Bounds.Equals(e.Bounds))
                changed.Add(e);
        }

        foreach (var (k, e) in prevByKey)
        {
            if (curByKey.ContainsKey(k)) continue;

            if (rawByKey != null && rawByKey.TryGetValue(k, out var rawEl))
            {
                var isOff = rawEl.Bounds.IsEmpty
                    || (rawEl.Props.TryGetValue("offscreen", out var v) && v is true or 1);
                if (isOff)
                    offscreen.Add(e);
                else
                    prunedByBudget.Add(e);
            }
            else
            {
                removed.Add(e);
            }
        }

        var notes = new List<string>();
        var pa = prev.ActiveWindow?.Title; var ca = cur.ActiveWindow?.Title;
        if (pa != ca) notes.Add($"active window: '{pa ?? "-"}' → '{ca ?? "-"}'");
        var pw = prev.Windows.Select(w => w.Title).ToHashSet();
        foreach (var w in cur.Windows)
            if (!pw.Contains(w.Title)) notes.Add($"window opened: '{w.Title}'");
        var cw = cur.Windows.Select(w => w.Title).ToHashSet();
        foreach (var w in prev.Windows)
            if (!cw.Contains(w.Title)) notes.Add($"window closed: '{w.Title}'");

        var summaryParts = new List<string>();
        if (added.Count > 0) summaryParts.Add($"{added.Count} added");
        if (removed.Count > 0) summaryParts.Add($"{removed.Count} removed");
        if (changed.Count > 0) summaryParts.Add($"{changed.Count} changed");
        if (offscreen.Count > 0) summaryParts.Add($"{offscreen.Count} scrolled offscreen");
        if (prunedByBudget.Count > 0) summaryParts.Add($"{prunedByBudget.Count} omitted by budget");
        if (summaryParts.Count > 0)
            notes.Add(string.Join(", ", summaryParts));

        return new ObsDelta(added, removed, changed,
            cur.ChangedRegions, notes, offscreen, prunedByBudget);
    }
}
