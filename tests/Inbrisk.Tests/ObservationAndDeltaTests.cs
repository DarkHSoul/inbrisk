using Inbrisk.Core;
using Inbrisk.Runtime;
using Xunit;

namespace Inbrisk.Tests;

public class ObservationAndDeltaTests
{
    private static UiElement MakeUiElement(
        string id,
        Role role,
        string name,
        RectPx bounds,
        string? autoId = null,
        IReadOnlyList<AncestryStep>? ancestry = null,
        long hwnd = 1001,
        Dictionary<string, object?>? props = null)
    {
        var recipe = new ReResolveRecipe(
            Pid: 1234,
            Hwnd: hwnd,
            OwnerTitle: "TestApp",
            Role: role,
            Name: name,
            AutomationId: autoId,
            AncestryPath: ancestry ?? Array.Empty<AncestryStep>(),
            LastBounds: bounds);

        var handle = new ElementHandle(BackendId.Uia, id, recipe);
        return new UiElement(
            Id: id,
            Source: BackendId.Uia,
            Role: role,
            Name: name,
            Bounds: bounds,
            Actions: new[] { "Click" },
            Props: props ?? new Dictionary<string, object?>(),
            Handle: handle,
            Pid: 1234,
            Hwnd: hwnd);
    }

    private static ObsElement MakeObs(UiElement e, string? value = null, string? state = null)
    {
        var key = ObservationBuilder.StableKey(e, e.Handle.Recipe.AutomationId);
        return new ObsElement(
            Id: e.Id,
            Role: e.Role.ToString(),
            Name: e.Name,
            Value: value,
            Bounds: e.Bounds,
            Actions: e.Actions,
            Source: e.Source.ToString(),
            State: state,
            StableKey: key);
    }

    private static AgentObservation MakeObservation(long id, IReadOnlyList<ObsElement> elements, long? baseId = null)
    {
        return new AgentObservation(
            ObservationId: id,
            At: DateTimeOffset.UtcNow,
            ActiveWindow: null,
            Windows: Array.Empty<ObsWindow>(),
            Elements: elements,
            RecentEvents: Array.Empty<ObservedEvent>(),
            Frames: Array.Empty<ObsFrameRef>(),
            ChangedRegions: Array.Empty<RectPx>(),
            CaptureBackend: "test",
            PrevAction: null,
            Delta: null,
            Stats: new ContextStats(0, 0, 0, 0, 0),
            BaseObservationId: baseId);
    }

    [Fact]
    public void StableKey_IgnoresCoordinates_PreservesIdentity()
    {
        // 1. Same AutomationId, different bounds (e.g. moved or scrolled)
        var el1 = MakeUiElement("e1", Role.Button, "Submit", new RectPx(10, 20, 100, 30), autoId: "btnSubmit");
        var el2 = MakeUiElement("e2", Role.Button, "Submit", new RectPx(500, 600, 100, 30), autoId: "btnSubmit");

        var key1 = ObservationBuilder.StableKey(el1);
        var key2 = ObservationBuilder.StableKey(el2);

        Assert.Equal(key1, key2);

        // 2. Different AutomationId -> different keys
        var el3 = MakeUiElement("e3", Role.Button, "Submit", new RectPx(10, 20, 100, 30), autoId: "btnCancel");
        var key3 = ObservationBuilder.StableKey(el3);
        Assert.NotEqual(key1, key3);

        // 3. Ancestry path fallback when AutomationId is null
        var ancestry = new[] { new AncestryStep(Role.Pane, "MainContainer", null, 0) };
        var el4 = MakeUiElement("e4", Role.ListItem, "Item 1", new RectPx(0, 0, 50, 20), ancestry: ancestry);
        var el5 = MakeUiElement("e5", Role.ListItem, "Item 1", new RectPx(0, 200, 50, 20), ancestry: ancestry);

        var key4 = ObservationBuilder.StableKey(el4);
        var key5 = ObservationBuilder.StableKey(el5);
        Assert.Equal(key4, key5);
    }

    [Fact]
    public void DeltaBuilder_Compute_AddedChangedRemoved()
    {
        var e1 = MakeUiElement("e1", Role.Button, "OK", new RectPx(10, 10, 50, 20), autoId: "btnOk");
        var e2 = MakeUiElement("e2", Role.Edit, "Name", new RectPx(10, 40, 100, 20), autoId: "txtInput");
        var e3 = MakeUiElement("e3", Role.Button, "Cancel", new RectPx(70, 10, 50, 20), autoId: "btnCancel");

        var obs1 = MakeObservation(1, new[] { MakeObs(e1), MakeObs(e2, value: "initial") });

        // In obs2:
        // e1 is unchanged
        // e2 changed value
        // e3 is added
        // e1 was in obs1, still in obs2
        var obs2 = MakeObservation(2, new[] { MakeObs(e1), MakeObs(e2, value: "updated"), MakeObs(e3) }, baseId: 1);

        var delta = DeltaBuilder.Compute(obs1, obs2);

        Assert.Single(delta.Added);
        Assert.Equal("btnCancel", delta.Added[0].Name == "Cancel" ? "btnCancel" : "");

        Assert.Single(delta.Changed);
        Assert.Equal("updated", delta.Changed[0].Value);

        Assert.Empty(delta.Removed);
        Assert.Empty(delta.Offscreen ?? Array.Empty<ObsElement>());
        Assert.Empty(delta.PrunedByBudget ?? Array.Empty<ObsElement>());
    }

    [Fact]
    public void DeltaBuilder_Compute_DistinguishesOffscreenAndBudget()
    {
        var eVisible = MakeUiElement("e1", Role.Button, "Play", new RectPx(10, 10, 50, 20), autoId: "btnPlay");
        var eOffscreen = MakeUiElement("e2", Role.ListItem, "Track 99", new RectPx(0, 0, 0, 0), autoId: "item99",
            props: new Dictionary<string, object?> { ["offscreen"] = true });
        var eBudgetPruned = MakeUiElement("e3", Role.ListItem, "Track 10", new RectPx(10, 100, 200, 20), autoId: "item10",
            props: new Dictionary<string, object?> { ["offscreen"] = false });
        var eDeleted = MakeUiElement("e4", Role.Button, "Delete", new RectPx(10, 200, 50, 20), autoId: "btnDel");

        // Previous observation had all 4 elements
        var prev = MakeObservation(1, new[]
        {
            MakeObs(eVisible),
            MakeObs(eOffscreen),
            MakeObs(eBudgetPruned),
            MakeObs(eDeleted)
        });

        // Current observation only has eVisible (eOffscreen and eBudgetPruned were filtered out of cur.Elements)
        var cur = MakeObservation(2, new[] { MakeObs(eVisible) }, baseId: 1);

        // Raw current scene from UIA tree has eVisible, eOffscreen, and eBudgetPruned (eDeleted is completely absent)
        var rawCurrent = new[] { eVisible, eOffscreen, eBudgetPruned };

        var delta = DeltaBuilder.Compute(prev, cur, rawCurrent);

        // eDeleted is not in rawCurrent at all -> Removed
        Assert.Single(delta.Removed);
        Assert.Equal("Delete", delta.Removed[0].Name);

        // eOffscreen was in prev, is in rawCurrent with offscreen = true -> Offscreen
        Assert.NotNull(delta.Offscreen);
        Assert.Single(delta.Offscreen);
        Assert.Equal("Track 99", delta.Offscreen[0].Name);

        // eBudgetPruned was in prev, is in rawCurrent with offscreen = false -> PrunedByBudget
        Assert.NotNull(delta.PrunedByBudget);
        Assert.Single(delta.PrunedByBudget);
        Assert.Equal("Track 10", delta.PrunedByBudget[0].Name);
    }

    [Fact]
    public void FindSpec_IncludeOffscreen_DefaultAndExplicit()
    {
        var specDefault = new FindSpec(Name: "Submit");
        Assert.False(specDefault.IncludeOffscreen);

        var specExplicit = new FindSpec(Name: "Track 100", IncludeOffscreen: true);
        Assert.True(specExplicit.IncludeOffscreen);
    }
}
