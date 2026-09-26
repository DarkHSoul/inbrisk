namespace Inbrisk.Core;

/// <summary>
/// Explicit state machine states for the Floating Activity Pill / HUD.
/// </summary>
public enum HudState
{
    Hidden,
    Standby,
    Entering,
    Working,
    Settling,
    SuccessBloom,
    Failure,
    Exiting,
    Emergency
}

/// <summary>
/// Snapshot of current HUD activity information.
/// </summary>
public sealed record HudActivityInfo(
    string Text,
    string? TargetApp,
    string? TargetIcon,
    HudState State,
    DateTimeOffset Timestamp);

/// <summary>
/// Helper to produce clean, high-level semantic activity labels for HUD display.
/// Prevents low-level spam and arbitrary layout breakage.
/// </summary>
public static class ActivityGranularity
{
    public const int MaxLabelLength = 50;

    /// <summary>
    /// Sanitizes arbitrary text into a clean single-line label under the character limit.
    /// </summary>
    public static string Sanitize(string? text, string fallback = "İşlem yapılıyor…")
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        var singleLine = text.Replace("\r", " ").Replace("\n", " ").Trim();
        while (singleLine.Contains("  ")) singleLine = singleLine.Replace("  ", " ");
        if (singleLine.Length <= MaxLabelLength) return singleLine;
        return singleLine[..(MaxLabelLength - 1)] + "…";
    }

    /// <summary>
    /// Derives a clean semantic status from an action and application/target name.
    /// E.g. ("Spotify", "launch") -> "Spotify açılıyor…"
    /// </summary>
    public static string FormatAction(string? target, string action)
    {
        var cleanTarget = Sanitize(target, "");
        var act = action.ToLowerInvariant().Trim();
        if (string.IsNullOrEmpty(cleanTarget))
        {
            return act switch
            {
                "launch" => "Uygulama açılıyor…",
                "find" => "Arama yapılıyor…",
                "click" => "Tıklanıyor…",
                "type" or "set_value" => "Yazılıyor…",
                "wait" or "wait_for" => "Bekleniyor…",
                _ => "İşlem yapılıyor…"
            };
        }

        return act switch
        {
            "launch" => $"{cleanTarget} açılıyor…",
            "find" or "search" => $"{cleanTarget} aranıyor…",
            "focus" => $"{cleanTarget} öne getiriliyor…",
            "close" => $"{cleanTarget} kapatılıyor…",
            _ => $"{cleanTarget} üzerinde çalışılıyor…"
        };
    }
}
