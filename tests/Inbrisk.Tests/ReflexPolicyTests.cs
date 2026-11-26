using Inbrisk.Runtime;

namespace Inbrisk.Tests;

[Trait("Category", "Unit")]
public sealed class ReflexPolicyTests
{
    private static ModalButton Btn(string name, long hwnd = 1001, string? autoId = null) =>
        new(name, autoId, hwnd);

    private static ModalInfo Modal(
        string? title = "Dialog",
        string? body = null,
        string? className = "#32770",
        long hwnd = 500,
        params ModalButton[] buttons) =>
        new(hwnd, title, className, 4242, "MainApp", buttons, body);

    [Theory]
    [InlineData(AutoDismissMode.Off)]
    [InlineData(AutoDismissMode.CloseOnly)]
    [InlineData(AutoDismissMode.Save)]
    [InlineData(AutoDismissMode.Discard)]
    public void Decide_UacAndSecuritySurfaces_AlwaysFailPlan_NeverAutoClick(AutoDismissMode mode)
    {
        var dangerousModals = new[]
        {
            Modal(title: "User Account Control", className: "$$Secure UAP Window", buttons: new[] { Btn("Yes"), Btn("No"), Btn("Cancel") }),
            Modal(title: "Kullanıcı Hesabı Denetimi", body: "Bu uygulamanın cihazınızda değişiklik yapmasına izin vermek istiyor musunuz?", buttons: new[] { Btn("Evet"), Btn("Hayır") }),
            Modal(title: "Windows Security", className: "Credential Dialog XAML Host", body: "Enter network credentials", buttons: new[] { Btn("OK"), Btn("Cancel") }),
            Modal(title: "Windows Güvenliği", body: "Ağ kimlik bilgisi girin", buttons: new[] { Btn("Tamam"), Btn("İptal") }),
            Modal(title: "Windows protected your PC", body: "Microsoft Defender SmartScreen", buttons: new[] { Btn("Don't run"), Btn("Close") }),
            Modal(title: "BitLocker", body: "Enter recovery key", buttons: new[] { Btn("Close") }),
            Modal(title: "Security Warning", body: "Do you want to install this certificate?", buttons: new[] { Btn("Yes"), Btn("No") }),
            Modal(title: "consent.exe", body: "Update available", buttons: new[] { Btn("Later") }),
        };

        foreach (var m in dangerousModals)
        {
            Assert.True(ReflexPolicy.IsDangerous(m), $"Expected IsDangerous=true for title='{m.Title}', class='{m.ClassName}'");
            var decision = ReflexPolicy.Decide(m, mode);
            Assert.Equal(ReflexAction.FailPlan, decision.Action);
            Assert.Null(decision.Button);
            Assert.Equal(100, decision.Confidence);
        }
    }

    [Fact]
    public void Decide_SaveConfirm_HonorsAllFourAutoDismissModes_EnglishAndTurkish()
    {
        var enModal = Modal(
            title: "Notepad",
            body: "Do you want to save your changes to Untitled?",
            buttons: new[] { Btn("&Save", 10), Btn("Do&n't Save", 11), Btn("Cancel", 12) });

        var dOff = ReflexPolicy.Decide(enModal, AutoDismissMode.Off);
        Assert.Equal(ReflexAction.FailPlan, dOff.Action);
        Assert.Equal(ModalDisposition.SaveConfirm, dOff.Disposition);
        Assert.Null(dOff.Button);

        var dSave = ReflexPolicy.Decide(enModal, AutoDismissMode.Save);
        Assert.Equal(ReflexAction.ClickButton, dSave.Action);
        Assert.Equal(ModalDisposition.SaveConfirm, dSave.Disposition);
        Assert.Equal(10, dSave.Button?.Hwnd);

        var dDiscard = ReflexPolicy.Decide(enModal, AutoDismissMode.Discard);
        Assert.Equal(ReflexAction.ClickButton, dDiscard.Action);
        Assert.Equal(11, dDiscard.Button?.Hwnd);

        var dCloseOnly = ReflexPolicy.Decide(enModal, AutoDismissMode.CloseOnly);
        Assert.Equal(ReflexAction.ClickButton, dCloseOnly.Action);
        Assert.Equal(12, dCloseOnly.Button?.Hwnd);

        // Turkish dotted/dotless İ/ı folding + accelerator markers
        var trModal = Modal(
            title: "Not Defteri",
            body: "Değişiklikleri kaydetmek istiyor musunuz?",
            buttons: new[] { Btn("_Kaydet", 20), Btn("Kaydetme", 21), Btn("İptal", 22) });

        Assert.Equal(20, ReflexPolicy.Decide(trModal, AutoDismissMode.Save).Button?.Hwnd);
        Assert.Equal(21, ReflexPolicy.Decide(trModal, AutoDismissMode.Discard).Button?.Hwnd);
        Assert.Equal(22, ReflexPolicy.Decide(trModal, AutoDismissMode.CloseOnly).Button?.Hwnd);
        Assert.Equal(ReflexAction.FailPlan, ReflexPolicy.Decide(trModal, AutoDismissMode.Off).Action);
    }

    [Fact]
    public void Decide_SaveConfirm_NeverClicksSaveAs()
    {
        var m = Modal(
            title: "Editor",
            body: "Unsaved changes will be lost",
            buttons: new[] { Btn("Save As", 10), Btn("Farklı kaydet", 11) });

        var decision = ReflexPolicy.Decide(m, AutoDismissMode.Save);
        Assert.Equal(ReflexAction.FailPlan, decision.Action);
        Assert.Null(decision.Button);
    }

    [Fact]
    public void Decide_UpdateNag_DefersOrCloses_NeverClicksOkOrInstall()
    {
        var deferrable = Modal(
            title: "Software Update",
            body: "A new version 4.2 is available!",
            buttons: new[] { Btn("Install Now", 10), Btn("Remind me later", 11) });

        var d = ReflexPolicy.Decide(deferrable, AutoDismissMode.Off);
        Assert.Equal(ReflexAction.ClickButton, d.Action);
        Assert.Equal(ModalDisposition.Update, d.Disposition);
        Assert.Equal(11, d.Button?.Hwnd);

        // If only OK / Install exist, MUST degrade to FailPlan rather than blind-clicking OK
        var okOnly = Modal(
            title: "Güncelleme Mevcut",
            body: "Yeni sürüm hazır",
            buttons: new[] { Btn("Tamam", 10), Btn("Şimdi Yükle", 11) });

        var dFail = ReflexPolicy.Decide(okOnly, AutoDismissMode.Off);
        Assert.Equal(ReflexAction.FailPlan, dFail.Action);
        Assert.Equal(ModalDisposition.Update, dFail.Disposition);
        Assert.Null(dFail.Button);
    }

    [Fact]
    public void Decide_ErrorDialog_ClicksCloseProgram()
    {
        var crash = Modal(
            title: "App.exe",
            body: "App.exe has stopped working",
            buttons: new[] { Btn("Debug", 10), Btn("Close the program", 11) });

        var d = ReflexPolicy.Decide(crash, AutoDismissMode.Off);
        Assert.Equal(ReflexAction.ClickButton, d.Action);
        Assert.Equal(ModalDisposition.Error, d.Disposition);
        Assert.Equal(11, d.Button?.Hwnd);
    }

    [Fact]
    public void Decide_CookieBanner_RequiresCookieContext_DoesNotAutoAcceptEula()
    {
        var cookie = Modal(
            title: "Privacy Notice",
            body: "We use cookies to improve your experience.",
            buttons: new[] { Btn("Manage settings", 10), Btn("Accept all", 11) });

        var dCookie = ReflexPolicy.Decide(cookie, AutoDismissMode.Off);
        Assert.Equal(ReflexAction.ClickButton, dCookie.Action);
        Assert.Equal(ModalDisposition.CookieBanner, dCookie.Disposition);
        Assert.Equal(11, dCookie.Button?.Hwnd);

        // EULA with "Accept all" but NO cookie/privacy/consent text must NOT be auto-clicked
        var eula = Modal(
            title: "End User License Agreement",
            body: "Please review the software license terms.",
            buttons: new[] { Btn("Accept all", 10), Btn("Decline", 11) });

        var dEula = ReflexPolicy.Decide(eula, AutoDismissMode.Off);
        Assert.Equal(ReflexAction.FailPlan, dEula.Action);
        Assert.Null(dEula.Button);
    }

    [Fact]
    public void Decide_UnknownAndGenericConfirmModals_AlwaysFailPlan()
    {
        var generic = Modal(
            title: "Confirm Action",
            body: "Delete 42 production records permanently?",
            buttons: new[] { Btn("Yes", 10), Btn("No", 11) });

        var dGeneric = ReflexPolicy.Decide(generic, AutoDismissMode.Discard);
        Assert.Equal(ReflexAction.FailPlan, dGeneric.Action);
        Assert.Equal(ModalDisposition.GenericConfirm, dGeneric.Disposition);

        var unknown = Modal(
            title: "Custom Widget",
            body: "Select an export profile",
            buttons: new[] { Btn("Profile A", 10), Btn("Profile B", 11) });

        var dUnknown = ReflexPolicy.Decide(unknown, AutoDismissMode.Save);
        Assert.Equal(ReflexAction.FailPlan, dUnknown.Action);
        Assert.Equal(ModalDisposition.Unknown, dUnknown.Disposition);
    }

    [Fact]
    public void Decide_ZeroHwnd_ReturnsIgnore()
    {
        var gone = Modal(title: "Vanished", hwnd: 0, buttons: new[] { Btn("OK", 10) });
        var d = ReflexPolicy.Decide(gone, AutoDismissMode.Off);
        Assert.Equal(ReflexAction.Ignore, d.Action);
        Assert.Null(d.Button);
    }
}
