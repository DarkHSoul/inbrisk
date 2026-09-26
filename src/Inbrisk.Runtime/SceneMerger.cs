using Inbrisk.Core;

namespace Inbrisk.Runtime;

/// <summary>
/// Merges UIA elements, OCR text spans and Vision elements into one desktop-space
/// element list.
///   - UIA is semantic truth: an OCR/Vision item duplicating a UIA element
///     (near-identical bounds, matching text) is dropped.
///   - Vision items *inside* a UIA element are kept — a rendered child object
///     (icon inside a button, sprite inside a canvas) is new information,
///     not a duplicate. Only near-total overlap counts as duplication.
/// </summary>
public static class SceneMerger
{
    /// <summary>IoU above which a non-UIA item is a duplicate of a UIA element.</summary>
    private const double DuplicateIou = 0.85;

    public static IReadOnlyList<UiElement> Merge(
        IReadOnlyList<UiElement> uia,
        IReadOnlyList<TextSpan> ocr,
        IReadOnlyList<UiElement> vision)
    {
        var merged = new List<UiElement>(uia);

        foreach (var span in ocr)
        {
            var dup = uia.Any(e => Covers(e.Bounds, span.Bounds) &&
                                  TextMatches(e, span.Text));
            if (!dup)
                merged.Add(new UiElement($"ocr_{merged.Count}", BackendId.Ocr,
                    Role.Text, span.Text, span.Bounds, ["click"],
                    new Dictionary<string, object?>
                    {
                        ["source"] = "ocr",
                        ["confidence"] = span.Confidence,
                    },
                    new ElementHandle(BackendId.Ocr, span.Text,
                        new ReResolveRecipe(null, null, null, Role.Text,
                            span.Text, null, [], span.Bounds)),
                    null, null, span.Confidence));
        }

        foreach (var v in vision)
        {
            var dup = uia.Any(e => Iou(e.Bounds, v.Bounds) > DuplicateIou &&
                                   (v.Name == null || TextMatches(e, v.Name)));
            if (!dup) merged.Add(v);
        }

        return merged;
    }

    /// <summary>Does 'outer' substantially contain 'inner'?</summary>
    public static bool Covers(RectPx outer, RectPx inner)
    {
        var inter = outer.Intersect(inner);
        if (inter.IsEmpty || inner.IsEmpty) return false;
        return (double)(inter.Width * inter.Height) / (inner.Width * inner.Height) > 0.6;
    }

    public static double Iou(RectPx a, RectPx b)
    {
        var inter = a.Intersect(b);
        if (inter.IsEmpty) return 0;
        var ia = (double)inter.Width * inter.Height;
        var ua = (double)a.Width * a.Height + (double)b.Width * b.Height - ia;
        return ua <= 0 ? 0 : ia / ua;
    }

    private static bool TextMatches(UiElement e, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (e.Name?.Contains(text, StringComparison.OrdinalIgnoreCase) == true) return true;
        if (text.Contains(e.Name ?? "\0", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(e.Name)) return true;
        return e.Props.TryGetValue("value", out var v) &&
               v?.ToString()?.Contains(text, StringComparison.OrdinalIgnoreCase) == true;
    }
}
