using System.Text;

namespace Inbrisk.Runtime;

/// <summary>High-level classification of an intercepted modal.</summary>
public enum ModalDisposition { Update, SaveConfirm, Error, CookieBanner, GenericConfirm, Unknown }

/// <summary>A single clickable control captured on a modal.</summary>
public sealed record ModalButton(string Name, string? AutomationId, long? Hwnd);

/// <summary>Snapshot of an intercepted modal window, produced by ReflexEngine.</summary>
public sealed record ModalInfo(long Hwnd, string? Title, string? ClassName, int Pid,
    string? OwnerTitle, IReadOnlyList<ModalButton> Buttons, string? BodyText);

/// <summary>What the engine should do with the modal.</summary>
public enum ReflexAction { Dismiss, ClickButton, FailPlan, Ignore }

/// <summary>Policy verdict handed back to ReflexEngine.</summary>
public sealed record ReflexDecision(ReflexAction Action, ModalDisposition Disposition,
    ModalButton? Button, string Reason, int Confidence);

/// <summary>User-configured behaviour for save-confirmation prompts.</summary>
public enum AutoDismissMode { Off, Save, Discard, CloseOnly }

/// <summary>
/// Modal reflex policy — pure data/logic over a captured modal snapshot.
/// No Win32, no IO, no mutable state. Fail-safe by construction:
/// security surfaces always FailPlan, unrecognized modals always FailPlan,
/// and any decided disposition without a concrete matched button degrades
/// to FailPlan (never a blind click). English + Turkish pattern tables;
/// matching runs in a folded space (see <see cref="Fold"/>).
/// </summary>
public static class ReflexPolicy
{
    // ---------------------------------------------------------------
    // Text normalization
    //
    // OrdinalIgnoreCase does NOT fold Turkish 'İ' (U+0130) onto 'i'/'I'
    // and 'ı' (U+0131) has culture-sensitive upper behaviour, so we fold
    // manually: strip accelerator markers ('&', '_'), uppercase with the
    // invariant culture, then collapse both Turkish i-variants onto 'I'.
    // Every pattern table below is pushed through the same Fold at
    // static-init, so all comparisons happen in folded space.
    // ---------------------------------------------------------------

    private static string Fold(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (ch is '&' or '_') continue; // accelerator markers: "&Save", "_Kaydet"
            sb.Append(ch);
        }
        return sb.ToString().ToUpperInvariant().Replace('İ', 'I').Replace('ı', 'I');
    }

    private static string[] FoldAll(params string[] patterns)
    {
        var folded = new string[patterns.Length];
        for (var i = 0; i < patterns.Length; i++) folded[i] = Fold(patterns[i]);
        return folded;
    }

    private static bool ContainsAny(string foldedHaystack, string[] foldedNeedles)
    {
        foreach (var n in foldedNeedles)
            if (foldedHaystack.Contains(n, StringComparison.Ordinal)) return true;
        return false;
    }

    // ---------------------------------------------------------------
    // Pattern tables (written in natural text; folded at static-init)
    // ---------------------------------------------------------------

    // -- Dangerous / security surfaces -------------------------------
    // UAC consent UI lives on the secure desktop; its window classes all
    // start with "$$Secure UAP". If ReflexEngine ever sees it, the plan
    // dies — we never click a security surface. (ModalInfo carries Pid,
    // not a process name, so consent.exe itself is matched via class.)
    private static readonly string[] DangerousClassPrefixes = FoldAll(
        "$$Secure UAP");

    private static readonly string[] DangerousClassContains = FoldAll(
        "Credential Dialog XAML Host",   // Windows credential picker
        "ConsolidatedView");              // BitLocker/security consolidate views

    private static readonly string[] DangerousTextContains = FoldAll(
        // UAC
        "User Account Control", "Kullanıcı Hesabı Denetimi",
        // Windows Security / credential prompts
        "Windows Security", "Windows Güvenliği",
        "Enter network credentials", "network credentials",
        "Ağ kimlik bilgisi", "kimlik bilgisi", "kimlik bilgileri",
        "Enter your credentials", "credentials",
        // SmartScreen
        "SmartScreen", "Windows protected your PC", "Bilgisayarınızı korudu",
        // BitLocker / device encryption
        "BitLocker", "sürücü şifreleme",
        // certificate trust prompts
        "certificate", "sertifika",
        // consent.exe leftovers (e.g. surfaced in a captured title)
        "consent.exe");

    // -- Update / nag dialogs ----------------------------------------
    private static readonly string[] UpdateTextContains = FoldAll(
        "update available", "an update is available", "updates available",
        "new version", "a new version", "update is ready", "update ready",
        "restart to update", "restart required to",
        "güncelleme", "güncelleştirme", "güncelleme mevcut",
        "yeni sürüm", "sürüm mevcut", "sürümü kullanılabilir");

    // Safe ways out of an update nag — defer or close. Deliberately no
    // "OK"/"Tamam": on an updater that can mean "yes, install now".
    private static readonly string[] DeferButtonExact = FoldAll(
        "Later", "Remind me later", "Remind me tomorrow", "Not now",
        "Ask me later", "Postpone", "Snooze", "Skip", "Skip this version",
        "Close", "Cancel", "No thanks",
        "Sonra", "Sonra hatırlat", "Daha sonra", "Şimdi değil", "Ertele",
        "Atla", "Bu sürümü atla", "Kapat", "İptal", "Vazgeç", "Hayır teşekkürler");

    private static readonly string[] DeferButtonContains = FoldAll(
        "later", "remind me", "not now", "sonra", "şimdi değil",
        "ertele", "atla", "skip");

    // -- Save-confirmation prompts ------------------------------------
    private static readonly string[] SaveTextContains = FoldAll(
        "do you want to save", "want to save your changes",
        "save changes", "save your changes", "unsaved changes",
        "changes will be lost",
        "kaydetmek istiyor", "değişiklikleri kaydet", "değişiklikler kaydedilsin",
        "kaydedilsin mi", "kaydedilmemiş değişiklik", "kaydedilmeyen değişiklikler",
        "değişiklikleriniz kaydedilmedi", "değişiklikler kaybolacak");

    // NB: "Save as"/"Farklı kaydet" deliberately EXCLUDED — that button
    // opens a nested save-location dialog rather than saving; clicking it
    // replaces one modal with another.
    private static readonly string[] SaveButtonExact = FoldAll(
        "Save", "Save all", "Kaydet", "Tümünü kaydet");

    private static readonly string[] DiscardButtonExact = FoldAll(
        "Don't Save", "Dont Save", "Do not save", "Don't save changes",
        "Discard", "Without saving",
        "Kaydetme", "Kaydetmeden", "At", "Vazgeç ve kapat");

    private static readonly string[] CancelButtonExact = FoldAll(
        "Cancel", "İptal", "Vazgeç");

    // -- Error / crash dialogs ----------------------------------------
    private static readonly string[] ErrorTextContains = FoldAll(
        "not responding", "stopped working", "has stopped working",
        "application error", "unhandled exception", "fatal error",
        "encountered a problem", "needs to close", "has crashed",
        "yanıt vermiyor", "çalışmayı durdurdu", "hata oluştu",
        "bir hata oluştu", "uygulama hatası", "işlenmemiş özel durum",
        "beklenmeyen bir hata", "bir sorunla karşılaştı",
        "kapatılması gerekiyor");

    private static readonly string[] ErrorButtonExact = FoldAll(
        "Close the program", "Close program", "End process",
        "Programı kapat", "İşlemi sonlandır",
        "Close", "Kapat", "OK", "Tamam");

    private static readonly string[] ErrorButtonContains = FoldAll(
        "close the program", "close program", "programı kapat",
        "işlemi sonlandır");

    // -- Cookie / consent banners --------------------------------------
    private static readonly string[] CookieTextContains = FoldAll(
        "cookie", "çerez", "tanımlama bilgisi",
        // real consent banners typically say "consent"/"privacy" rather
        // than the word cookie — required for strong-accept context.
        "consent", "privacy", "gizlilik", "onayınız", "verileri kabul");

    // Unmistakable consent controls — safe to click without context.
    private static readonly string[] CookieStrongExact = FoldAll(
        "Accept all", "Accept all cookies", "Accept cookies",
        "Tümünü kabul et", "Çerezleri kabul et", "Hepsini kabul et",
        "Tüm çerezleri kabul et");

    private static readonly string[] CookieStrongContains = FoldAll(
        "accept all", "accept cookies", "tümünü kabul",
        "çerezleri kabul", "hepsini kabul");

    // Weaker accept words — only trusted when the dialog also mentions
    // cookies/çerez, so "Kabul et" on a license prompt stays GenericConfirm.
    private static readonly string[] CookieWeakExact = FoldAll(
        "Accept", "I agree", "Agree", "Agree and continue", "Allow all",
        "Kabul et", "Kabul ediyorum", "İzin ver", "Tümüne izin ver");

    // -- Generic confirm shape -----------------------------------------
    private static readonly string[] ConfirmButtonNames = FoldAll(
        "Yes", "No", "OK", "Cancel", "Retry", "Continue", "Close",
        "Evet", "Hayır", "Tamam", "İptal", "Vazgeç",
        "Yeniden dene", "Devam", "Devam et", "Kapat");

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private static ModalButton? FindButton(ModalInfo modal,
        string[] foldedExact, string[]? foldedContains = null)
    {
        var buttons = modal.Buttons;
        if (buttons == null || buttons.Count == 0) return null;

        // Exact folded-name match first — a fuzzy hit is never enough to click.
        foreach (var cand in foldedExact)
            foreach (var b in buttons)
                if (b != null &&
                    (Fold(b.Name) == cand || Fold(b.AutomationId) == cand))
                    return b;

        if (foldedContains != null)
            foreach (var cand in foldedContains)
                foreach (var b in buttons)
                    if (b != null &&
                        Fold(b.Name).Contains(cand, StringComparison.Ordinal))
                        return b;

        return null;
    }

    private static string Haystack(ModalInfo modal) =>
        Fold(modal.Title) + "\n" + Fold(modal.BodyText) + "\n" + Fold(modal.OwnerTitle);

    private static string DescribeButtons(ModalInfo modal)
    {
        var buttons = modal.Buttons;
        if (buttons == null || buttons.Count == 0) return "<none>";
        var names = new List<string>(buttons.Count);
        foreach (var b in buttons)
            if (b != null && !string.IsNullOrWhiteSpace(b.Name))
                names.Add(b.Name.Trim());
        return names.Count == 0 ? "<none>" : string.Join(", ", names);
    }

    private static ReflexDecision Click(ModalDisposition d, ModalButton b, string why, int conf) =>
        new(ReflexAction.ClickButton, d, b, why, conf);

    private static ReflexDecision Fail(ModalDisposition d, string why, int conf) =>
        new(ReflexAction.FailPlan, d, null, why, conf);

    private static ReflexDecision ClickOrFail(ModalInfo modal, ModalDisposition d,
        string[] foldedExact, string[]? foldedContains, string why, int conf)
    {
        var b = FindButton(modal, foldedExact, foldedContains);
        return b != null
            ? Click(d, b, $"{why} → '{b.Name}'", conf)
            : Fail(d, $"{why} — no matching button (seen: {DescribeButtons(modal)})", conf);
    }

    // ---------------------------------------------------------------
    // Policy
    // ---------------------------------------------------------------

    /// <summary>
    /// True for security surfaces — UAC consent, credential prompts,
    /// Windows Security / SmartScreen / BitLocker / certificate dialogs.
    /// These are NEVER auto-clicked; <see cref="Decide"/> maps them to
    /// FailPlan before any other classification.
    /// </summary>
    public static bool IsDangerous(ModalInfo modal)
    {
        if (modal == null) return false;

        var cls = Fold(modal.ClassName);
        foreach (var p in DangerousClassPrefixes)
            if (cls.StartsWith(p, StringComparison.Ordinal)) return true;
        foreach (var c in DangerousClassContains)
            if (cls.Contains(c, StringComparison.Ordinal)) return true;

        return ContainsAny(Haystack(modal), DangerousTextContains);
    }

    /// <summary>
    /// Classify the modal and pick an action. Decision order:
    /// gone → dangerous → update → save-confirm → error → cookie →
    /// generic-confirm → unknown. Anything past "dangerous" that wants a
    /// click but finds no matching concrete button degrades to FailPlan.
    /// </summary>
    public static ReflexDecision Decide(ModalInfo modal, AutoDismissMode saveMode)
    {
        ArgumentNullException.ThrowIfNull(modal);

        // Modal already vanished — nothing to do.
        if (modal.Hwnd <= 0)
            return new ReflexDecision(ReflexAction.Ignore, ModalDisposition.Unknown,
                null, "modal already gone (hwnd 0)", 100);

        // 1) Security surfaces first — always fail, never click.
        if (IsDangerous(modal))
            return Fail(ModalDisposition.Unknown,
                "security surface (UAC/credential/security dialog) — never auto-clicked; " +
                $"title='{modal.Title}'", 100);

        var hay = Haystack(modal);

        // 2) Update / nag dialog → defer or close it.
        if (ContainsAny(hay, UpdateTextContains))
            return ClickOrFail(modal, ModalDisposition.Update,
                DeferButtonExact, DeferButtonContains,
                "update nag", 85);

        // 3) Save confirmation → honour saveMode.
        var isSaveDialog = ContainsAny(hay, SaveTextContains) ||
            (FindButton(modal, SaveButtonExact) != null &&
             FindButton(modal, DiscardButtonExact) != null);
        if (isSaveDialog)
        {
            switch (saveMode)
            {
                case AutoDismissMode.Save:
                    return ClickOrFail(modal, ModalDisposition.SaveConfirm,
                        SaveButtonExact, null, "auto-save", 90);
                case AutoDismissMode.Discard:
                    return ClickOrFail(modal, ModalDisposition.SaveConfirm,
                        DiscardButtonExact, null, "auto-discard", 90);
                case AutoDismissMode.CloseOnly:
                    // Don't lose work, don't continue the close — cancel out.
                    return ClickOrFail(modal, ModalDisposition.SaveConfirm,
                        CancelButtonExact, null,
                        "CloseOnly → cancel save prompt (keep work, abort close)", 85);
                default: // Off
                    return Fail(ModalDisposition.SaveConfirm,
                        $"save confirmation, auto-dismiss off (buttons: {DescribeButtons(modal)})", 90);
            }
        }

        // 4) Error / crash → close it; engine reports the crash to the plan.
        if (ContainsAny(hay, ErrorTextContains))
            return ClickOrFail(modal, ModalDisposition.Error,
                ErrorButtonExact, ErrorButtonContains,
                "error/crash dialog", 85);

        // 5) Cookie / consent banner → accept. BOTH the strong and the weak
        // accept paths require cookie/consent text in the dialog — a bare
        // "Accept all"-shaped caption on a license/EULA prompt must not be
        // reflexively agreed to.
        var cookieText = ContainsAny(hay, CookieTextContains);
        var cookieBtn = cookieText
            ? FindButton(modal, CookieStrongExact, CookieStrongContains)
                ?? FindButton(modal, CookieWeakExact)
            : null;
        if (cookieBtn != null)
            return Click(ModalDisposition.CookieBanner, cookieBtn,
                $"cookie/consent banner → '{cookieBtn.Name}'", 80);
        if (ContainsAny(hay, CookieTextContains))
            return Fail(ModalDisposition.CookieBanner,
                $"cookie banner without an accept control (buttons: {DescribeButtons(modal)})", 60);

        // 6) Recognizable yes/no/ok shape but unclassified intent → plan decides.
        var buttons = modal.Buttons;
        if (buttons != null && buttons.Count > 0)
        {
            foreach (var b in buttons)
            {
                if (b == null) continue;
                var name = Fold(b.Name);
                foreach (var c in ConfirmButtonNames)
                {
                    if (name == c)
                        return Fail(ModalDisposition.GenericConfirm,
                            $"unclassified confirm-style dialog; buttons: {DescribeButtons(modal)}", 40);
                }
            }
        }

        // 7) Fully unrecognized — fail-safe, report what we saw.
        return Fail(ModalDisposition.Unknown,
            $"unrecognized modal; title='{modal.Title}' class='{modal.ClassName}' " +
            $"buttons: {DescribeButtons(modal)}", 10);
    }
}
