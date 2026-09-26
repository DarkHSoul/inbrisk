using Inbrisk.Core;
using Xunit;

namespace Inbrisk.Tests;

[Collection("desktop")]
public class ActionTests
{
    private readonly DesktopFixture _fx;
    public ActionTests(DesktopFixture fx)
    {
        _fx = fx;
        _fx.Inbrisk.Focus(_fx.Hwnd);
        DesktopFixture.WriteState("idle");
    }

    private UiElement FindOne(FindSpec spec) =>
        Assert.Single(_fx.Inbrisk.Find(spec));

    [Fact]
    public void Invoke_SaveButton_WritesStateFile()
    {
        var save = FindOne(new FindSpec(Hwnd: _fx.Hwnd, AutomationId: "SaveButton"));
        var r = _fx.Inbrisk.Invoke(save.Id);
        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal(BackendId.Uia, r.BackendUsed);
        Assert.Equal("UIA.InvokePattern", r.Method);

        // verify by app state, not API success
        SpinWait.SpinUntil(() =>
            (DesktopFixture.ReadState() ?? "").StartsWith("saved"), 3000);
        Assert.StartsWith("saved", DesktopFixture.ReadState() ?? "");
    }

    [Fact]
    public void SetValue_ThenInvoke_RoundTripsText()
    {
        var box = FindOne(new FindSpec(Hwnd: _fx.Hwnd, AutomationId: "MainTextBox"));
        var r = _fx.Inbrisk.SetValue(box.Id, "hello-inbrisk");
        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal("UIA.ValuePattern", r.Method);

        var save = FindOne(new FindSpec(Hwnd: _fx.Hwnd, AutomationId: "SaveButton"));
        _fx.Inbrisk.Invoke(save.Id);
        SpinWait.SpinUntil(() => (DesktopFixture.ReadState() ?? "").Contains("hello-inbrisk"), 3000);
        Assert.Equal("saved:hello-inbrisk", DesktopFixture.ReadState() ?? "");
    }

    [Fact]
    public void Toggle_CheckBox_VerifiedByStateProp()
    {
        var check = FindOne(new FindSpec(Hwnd: _fx.Hwnd, AutomationId: "EnableCheck"));
        var before = check.Props.TryGetValue("toggleState", out var ts) ? ts?.ToString() : null;

        var r = _fx.Inbrisk.Toggle(check.Id);
        Assert.True(r.Success, r.ErrorMessage);

        var after = FindOne(new FindSpec(Hwnd: _fx.Hwnd, AutomationId: "EnableCheck"));
        var afterState = after.Props.TryGetValue("toggleState", out var ts2) ? ts2?.ToString() : null;
        Assert.NotEqual(before, afterState);
    }

    [Fact]
    public void Invoke_DisabledButton_FailsHonestly()
    {
        var disabled = FindOne(new FindSpec(Hwnd: _fx.Hwnd, AutomationId: "DisabledButton"));
        var r = _fx.Inbrisk.Invoke(disabled.Id);
        Assert.False(r.Success);
        Assert.Equal(ErrorCode.Disabled, r.Error);
    }

    [Fact]
    public void TypeText_IntoElement_TypesViaSendInput()
    {
        var box = FindOne(new FindSpec(Hwnd: _fx.Hwnd, AutomationId: "MainTextBox"));
        var r = _fx.Inbrisk.Type(box.Id, "typed-xyz");
        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal("SendInput.type", r.Method);

        var save = FindOne(new FindSpec(Hwnd: _fx.Hwnd, AutomationId: "SaveButton"));
        _fx.Inbrisk.Invoke(save.Id);
        SpinWait.SpinUntil(() => (DesktopFixture.ReadState() ?? "").Contains("typed-xyz"), 3000);
    }

    [Fact]
    public void ClickAt_CoordinateClick_OnSaveButton()
    {
        _fx.EnsureForeground();
        var save = FindOne(new FindSpec(Hwnd: _fx.Hwnd, AutomationId: "SaveButton"));
        var c = save.Center;
        var r = _fx.Inbrisk.ClickAt(c.X, c.Y);
        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal("SendInput.click", r.Method);

        SpinWait.SpinUntil(() =>
            (DesktopFixture.ReadState() ?? "").StartsWith("saved"), 3000);
        var st = DesktopFixture.ReadState() ?? "";
        Assert.True(st.StartsWith("saved"),
            $"state={st} topAtClick={_fx.TopWindowAt(c.X, c.Y)}");
    }

    [Fact]
    public void Select_ListItem_ViaSelectionPattern()
    {
        var item = FindOne(new FindSpec(Hwnd: _fx.Hwnd, Name: "ItemTwo", Role: Role.ListItem));
        var r = _fx.Inbrisk.Perform(new ActionIntent(ActionKind.Select, TargetRef.Element(item.Id)));
        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal("UIA.SelectionItemPattern", r.Method);
    }

    [Fact]
    public void StaleElement_ReResolves_AfterDialogReopen()
    {
        _fx.CloseDialogs();
        var dlg = FindOne(new FindSpec(Hwnd: _fx.Hwnd, AutomationId: "DialogButton"));
        _fx.Inbrisk.Invoke(dlg.Id);
        var close = _fx.Inbrisk.WaitForElement(
            new FindSpec(WindowTitle: "TestDialog", AutomationId: "CloseDialog"));
        Assert.True(close.Success);
        var closeBtn = Assert.Single(_fx.Inbrisk.Find(
            new FindSpec(WindowTitle: "TestDialog", AutomationId: "CloseDialog")));
        var id = closeBtn.Id;

        // close the dialog — element dies
        _fx.Inbrisk.Invoke(id);
        Thread.Sleep(400);

        // reopen — same id must re-resolve to the new element instance
        var dlg2 = FindOne(new FindSpec(Hwnd: _fx.Hwnd, AutomationId: "DialogButton"));
        _fx.Inbrisk.Invoke(dlg2.Id);
        var reopened = _fx.Inbrisk.WaitForElement(
            new FindSpec(WindowTitle: "TestDialog", AutomationId: "CloseDialog"));
        Assert.True(reopened.Success);

        var alive = _fx.Inbrisk.Element(id);
        Assert.NotNull(alive);
        var r = _fx.Inbrisk.Invoke(id); // executor: EnsureAlive → re-resolve → invoke
        Assert.True(r.Success, r.ErrorMessage);
        _fx.CloseDialogs();
    }
}
