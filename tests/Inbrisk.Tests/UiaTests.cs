using System.Diagnostics;
using Inbrisk.Core;
using Xunit;

namespace Inbrisk.Tests;

[Collection("desktop")]
public class UiaTests
{
    private readonly DesktopFixture _fx;
    public UiaTests(DesktopFixture fx) => _fx = fx;

    [Fact]
    public void Inspect_ReturnsPrunedTree_WithKnownControls()
    {
        var els = _fx.Inbrisk.Inspect(_fx.Hwnd);
        Assert.NotEmpty(els);
        Assert.True(els.Count <= 500, "pruning cap not enforced");

        var save = els.FirstOrDefault(e => e.Name == "Save");
        Assert.NotNull(save);
        Assert.Equal(Role.Button, save.Role);
        Assert.Contains("invoke", save.Actions);
        Assert.True(save.Bounds.Width > 0);

        var textbox = els.FirstOrDefault(e => e.Name == "Main text box");
        Assert.NotNull(textbox);
        Assert.Equal(Role.Edit, textbox.Role);
    }

    [Fact]
    public void Find_ByRoleAndName()
    {
        var found = _fx.Inbrisk.Find(new FindSpec(Hwnd: _fx.Hwnd, Role: Role.Button, Name: "Save"));
        Assert.Single(found);
        Assert.Equal("Save", found[0].Name);
    }

    [Fact]
    public void Find_ByAutomationId()
    {
        var found = _fx.Inbrisk.Find(new FindSpec(Hwnd: _fx.Hwnd, AutomationId: "MainTextBox"));
        Assert.Single(found);
        Assert.Equal(Role.Edit, found[0].Role);
    }

    [Fact]
    public void Find_NoMatch_ReturnsEmpty()
    {
        var found = _fx.Inbrisk.Find(new FindSpec(Hwnd: _fx.Hwnd, Name: "NoSuchControlXYZ"));
        Assert.Empty(found);
    }

    [Fact]
    public void Element_HasReResolveRecipe()
    {
        var found = _fx.Inbrisk.Find(new FindSpec(Hwnd: _fx.Hwnd, AutomationId: "SaveButton"));
        var el = Assert.Single(found);
        Assert.NotNull(el.Handle.Recipe);
        Assert.Equal(Role.Button, el.Handle.Recipe.Role);
        Assert.NotEmpty(el.Handle.Recipe.AncestryPath);
    }

    // ---------------- scope regression guardrails ----------------

    /// <summary>Read the find telemetry appended since <paramref name="mark"/>
    /// bytes into the file — returns the raw tail for the caller to parse.</summary>
    private static string PerfTail(long mark)
    {
        var file = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "inbrisk", "find-perf.jsonl");
        if (!File.Exists(file)) return "";
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite);
        fs.Seek(Math.Min(mark, fs.Length), SeekOrigin.Begin);
        using var r = new StreamReader(fs);
        return r.ReadToEnd();
    }

    private static long PerfMark()
    {
        var file = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "inbrisk", "find-perf.jsonl");
        return File.Exists(file) ? new FileInfo(file).Length : 0;
    }

    /// <summary>Last uia.find record's field as JsonElement, or null.</summary>
    private static System.Text.Json.JsonElement? LastFindField(string tail,
        string field)
    {
        var line = tail.Split('\n')
            .LastOrDefault(l => l.Contains("\"uia.find\""));
        if (line == null) return null;
        using var doc = System.Text.Json.JsonDocument.Parse(line);
        return doc.RootElement.TryGetProperty(field, out var v)
            ? v.Clone() : null;
    }

    [Fact]
    public void Within_ScopeElement_SearchesSubtree_NeverDesktop()
    {
        // the canonical regression: within:$ref must reach the backend as
        // ScopeElementId and scope UIA enumeration to that subtree. If the
        // chain breaks, the find silently enumerates the DESKTOP — which is
        // what made the $artist step cost ~6.8s in the perf pass.
        var list = _fx.Inbrisk.Find(new FindSpec(Hwnd: _fx.Hwnd,
            Role: Role.List)).First();
        var mark = PerfMark();

        var items = _fx.Inbrisk.Find(new FindSpec(
            ScopeElementId: list.Handle.BackendRef, Role: Role.ListItem));
        Assert.True(items.Count >= 3, "list subtree should yield its items");

        var tail = PerfTail(mark);
        var scope = LastFindField(tail, "scopeType");
        Assert.True(scope is { } s && s.GetString() == "element",
            $"within scope must resolve to 'element', got " +
            $"{scope?.ToString() ?? "no telemetry"} — a desktop/global " +
            "scope here is the regression this test exists to catch");
        var roots = LastFindField(tail, "roots");
        Assert.True(roots is { } r && r.GetInt32() == 1,
            $"element scope = exactly one subtree root, got {roots}");
        // desktop enumeration produces hundreds of candidates; the TestApp
        // list subtree has a handful — a hard ceiling catches the fallback
        var enumCount = LastFindField(tail, "candidatesEnumerated");
        Assert.True(enumCount is { } e && e.GetInt32() < 500,
            $"subtree find enumerated {enumCount} candidates — " +
            "desktop-global fallback suspected");
    }

    [Fact]
    public void Stale_ScopeElement_FallsBack_ToWiderScope_NotWrongResult()
    {
        // a DEAD ScopeElementId must widen (hwnd/pid/global), not silently
        // return the wrong subtree — correctness preserved, telemetry shows
        // the fallback scope instead of 'element'
        var items = _fx.Inbrisk.Find(new FindSpec(
            ScopeElementId: "uia_definitely_dead_999",
            Hwnd: _fx.Hwnd, Role: Role.ListItem));
        Assert.NotEmpty(items); // fell back to the hwnd scope
    }

    // ---------------- UIA threading audit ----------------

    /// <summary>Every UIA COM call must run on ONE dedicated MTA worker —
    /// never the caller's thread (xunit runs tests on a thread pool /
    /// possibly STA). Element objects held in the live map are only ever
    /// touched there, so apartment lifetime can't strand them.</summary>
    [Fact]
    public void UiaDispatcher_SingleDedicatedMtaThread()
    {
        using var disp = new Inbrisk.Platform.Windows.Uia.UiaDispatcher();
        var t1 = disp.Run(_ => (
            Thread.CurrentThread.GetApartmentState(),
            Thread.CurrentThread.Name,
            Environment.CurrentManagedThreadId,
            Thread.CurrentThread.IsBackground));
        var t2 = disp.Run(_ => Environment.CurrentManagedThreadId);
        var t3 = disp.Run(_ => Environment.CurrentManagedThreadId);
        Assert.Equal(ApartmentState.MTA, t1.Item1);
        Assert.Equal("inbrisk-uia", t1.Item2);
        Assert.True(t1.Item3 != Environment.CurrentManagedThreadId,
            "UIA work ran on the caller's thread");
        Assert.True(t1.Item4, "dispatcher thread must be background");
        Assert.Equal(t1.Item3, t2);
        Assert.Equal(t1.Item3, t3); // same worker every call — no churn
    }

    [Fact]
    public async Task UiaDispatcher_CancelledCall_NeverExecutes()
    {
        using var disp = new Inbrisk.Platform.Windows.Uia.UiaDispatcher();
        using var blockerStarted = new ManualResetEventSlim(false);
        using var unblock = new ManualResetEventSlim(false);

        // 1. Queue a work item that holds the MTA thread briefly
        var blockerTask = Task.Run(() =>
        {
            disp.Run(_ =>
            {
                blockerStarted.Set();
                unblock.Wait(2000);
                return true;
            });
        });

        Assert.True(blockerStarted.Wait(1000), "blocker failed to start on dispatcher");

        // 2. Queue a work item with a token that we cancel while it waits in the queue
        using var cts = new CancellationTokenSource();
        var executed = false;
        var pendingTask = Task.Run(() =>
        {
            return disp.Run(_ =>
            {
                executed = true;
                return 42;
            }, ct: cts.Token);
        });

        // Cancel while it's in the queue
        cts.Cancel();
        unblock.Set(); // unblock first item so dispatcher dequeues the second

        // The pending item must throw OperationCanceledException
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pendingTask);
        // And its COM action MUST NEVER have executed!
        Assert.False(executed, "Cancelled work item must never execute on UiaDispatcher");
    }

    [Fact]
    public async Task UiaDispatcher_PurgeQueue_CancelsPendingWork_WithoutExecuting()
    {
        using var disp = new Inbrisk.Platform.Windows.Uia.UiaDispatcher();
        using var blockerStarted = new ManualResetEventSlim(false);
        using var unblock = new ManualResetEventSlim(false);

        _ = Task.Run(() =>
        {
            disp.Run(_ =>
            {
                blockerStarted.Set();
                unblock.Wait(2000);
                return true;
            });
        });

        Assert.True(blockerStarted.Wait(1000));

        var executed1 = false;
        var executed2 = false;

        var t1 = Task.Run(() => disp.Run(_ => { executed1 = true; return 1; }));
        var t2 = Task.Run(() => disp.Run(_ => { executed2 = true; return 2; }));

        var swWait = Stopwatch.StartNew();
        while (disp.QueuedCount < 2 && swWait.ElapsedMilliseconds < 2000)
            Thread.Sleep(5);
        Assert.Equal(2, disp.QueuedCount);

        // Purge queue while blocker is still running
        disp.PurgeQueue();
        unblock.Set();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t1);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t2);

        Assert.False(executed1, "Purged work item 1 must not execute");
        Assert.False(executed2, "Purged work item 2 must not execute");
    }

    [Fact]
    public void Cancelled_PerformNative_DoesNotMutate_TargetApp()
    {
        DesktopFixture.WriteState("idle");

        var save = _fx.Inbrisk.Find(new FindSpec(Hwnd: _fx.Hwnd, AutomationId: "SaveButton")).First();
        Assert.NotNull(save);

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancelled token

        var actx = new ActionContext(cts.Token, null, "test-session", null, null);
        var result = _fx.Inbrisk.Parts.Executor.Perform(
            new ActionIntent(ActionKind.Invoke, TargetRef.Element(save.Id)), actx);

        Assert.False(result.Success, "Pre-cancelled action must not report success");
        // Verify target app never saw the action: state file stayed "idle"
        var state = DesktopFixture.ReadState();
        Assert.Equal("idle", state);
    }
}
