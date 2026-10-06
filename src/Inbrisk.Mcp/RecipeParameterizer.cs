using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Inbrisk.Core;

namespace Inbrisk.Mcp;

/// <summary>
/// Turns literal RunStep payloads into <c>{{parameter}}</c> placeholders for the
/// auto-recipe hook, paired with the literal→name map that
/// <c>computer_run_recipe</c> later substitutes via SubstituteInJsonNode.
///
/// Conservative by design: a false parameter silently corrupts a recipe (a
/// structural literal gets replaced per-run), while a missed parameter only
/// leaves a literal behind. Anything ambiguous stays literal.
///
/// Parameterized (first-seen order, ≤ <see cref="MaxParameters"/>):
///   text/value/url args containing an https?:// URL           → {{url}}, {{url2}}…
///   text/value/path/exe/arg fields containing a drive-letter  → {{path}}, {{path2}}…
///     absolute path ending in an extension
///   type/set_value payloads that look like free text          → {{text}}, {{text2}}…
///     (not control chars, not key names, not hotkey combos,
///      not the recipe's own launch app name)
///   Same literal seen again → same parameter; assert Contains/Exact fields
///   matching an already-collected literal are rewritten for consistency but
///   never create new parameters.
///
/// NEVER parameterized: target names/selectors (t, map:, automationId, role,
/// name, within…), key/keys/modifiers, ms/timeouts/counts, hwnd/elementIds/
/// $refs/frame+observation ids, app names, adapter args, wait_for queries.
/// </summary>
public static class RecipeParameterizer
{
    /// <summary>Hard cap on extracted parameters — beyond this, literals stay literal.</summary>
    public const int MaxParameters = 16;

    /// <summary>Rewritten steps plus the {{name}} → original-literal map.</summary>
    public sealed record ParameterizeResult(
        InbriskTools.RunStep[] Steps,
        Dictionary<string, string> Params)
    {
        /// <summary>True when at least one placeholder was introduced.</summary>
        public bool HasParameters => Params.Count > 0;
    }

    // ------------------------------------------------------------------ API

    /// <summary>
    /// Scan <paramref name="steps"/> (recursively through for_each/scan sub-steps),
    /// collect distinct parameterizable literals in first-seen order, and return a
    /// rewritten step array where those literals are <c>{{name}}</c> placeholders.
    /// Input steps are never mutated (RunStep is a record — everything is `with`-copied).
    /// </summary>
    public static ParameterizeResult Extract(IReadOnlyList<InbriskTools.RunStep> steps)
    {
        if (steps == null || steps.Count == 0)
            return new ParameterizeResult(Array.Empty<InbriskTools.RunStep>(), new Dictionary<string, string>());

        // Pre-pass: launch app names are structural — a typed "Spotify" matching
        // the recipe's own app is never a parameter.
        var appNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in steps)
            if (s != null)
                CollectAppNames(s, appNames);

        // Pass 1 — collect distinct literals in first-seen order.
        var collector = new Collector();
        foreach (var s in steps)
            if (s != null)
                CollectStep(s, appNames, collector);

        if (collector.Total == 0)
            return new ParameterizeResult(steps.ToArray(), new Dictionary<string, string>());

        var names = collector.FinalizeNames(); // literal → {{name}} rewrite tables

        // Pass 2 — rewrite (never mutates input records).
        var rewritten = new InbriskTools.RunStep[steps.Count];
        for (var i = 0; i < steps.Count; i++)
            rewritten[i] = steps[i] == null ? null! : RewriteStep(steps[i], names);

        return new ParameterizeResult(rewritten, names.Params);
    }

    /// <summary>
    /// Human-readable parameter list for result payloads, e.g.
    /// <c>{{url}} = "https://open.spotify.com/…"</c> — one per line, in
    /// first-seen order. Returns "(no parameters)" when empty.
    /// </summary>
    public static string Describe(IReadOnlyDictionary<string, string> parameters)
    {
        if (parameters == null || parameters.Count == 0)
            return "(no parameters)";

        var sb = new StringBuilder();
        foreach (var (name, literal) in parameters)
            sb.Append("{{").Append(name).Append("}} = \"").Append(literal).Append('"').AppendLine();
        return sb.ToString().TrimEnd();
    }

    /// <summary>Convenience overload of <see cref="Describe(IReadOnlyDictionary{string, string})"/>.</summary>
    public static string Describe(ParameterizeResult result) =>
        Describe(result?.Params ?? new Dictionary<string, string>());

    /// <summary>
    /// Bridge to <c>computer_save_recipe</c>'s <paramref name="result"/> parameters arg:
    /// one <see cref="RecipeParameter"/> per extracted parameter with a generated
    /// kind description ("auto-extracted URL" / "auto-extracted file path" / "auto-extracted text").
    /// </summary>
    public static RecipeParameter[] ToRecipeParameters(ParameterizeResult result)
    {
        if (result == null || result.Params.Count == 0)
            return Array.Empty<RecipeParameter>();
        var list = new RecipeParameter[result.Params.Count];
        var i = 0;
        foreach (var (name, literal) in result.Params)
            list[i++] = new RecipeParameter(name, $"auto-extracted {Classify(literal)}");
        return list;
    }

    // ------------------------------------------------------------- patterns

    // http(s) URL — stops at whitespace, quotes, brackets and braces so a
    // "{{…}}" placeholder or JSON delimiter can never bleed into the match.
    private static readonly Regex UrlRe = new(
        @"https?://[^\s""'<>\[\]{}()]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Drive-letter absolute path ending in ".ext" (1-8 alnum). ':' is excluded
    // from segment chars so adjacent paths can't merge across a space.
    private static readonly Regex PathRe = new(
        @"(?<![A-Za-z0-9_])[A-Za-z]:\\(?:[^\\/:*?""<>|\x00-\x1F]+\\)*[^\\/:*?""<>|\x00-\x1F]*\.[A-Za-z0-9]{1,8}",
        RegexOptions.Compiled);

    // Pure SendKeys-style brace sequences: "{ENTER} {TAB}", "{F4}".
    private static readonly Regex BraceSeqRe = new(
        @"^(\{[^{}]+\}\s*)+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Trailing characters trimmed from a URL match — legal in URLs in theory,
    // but overwhelmingly sentence/closing punctuation in practice.
    private static readonly char[] UrlTrailers = { '.', ',', ';', ':', '!', '?', '\'', '"' };

    // Single tokens that read as a key press rather than typed words.
    private static readonly HashSet<string> KeyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // KeyCode member names (Inbrisk.Core.Services) + common aliases
        "enter", "return", "escape", "esc", "tab", "space", "backspace", "back",
        "delete", "del", "insert", "ins",
        "left", "right", "up", "down", "home", "end",
        "pageup", "pagedown", "pgup", "pgdn",
        "ctrl", "control", "shift", "alt", "option", "win", "windows", "cmd", "meta",
        "lctrl", "rctrl", "lshift", "rshift", "lalt", "ralt", "lwin", "rwin",
        "pause", "break", "capslock", "numlock", "scrolllock",
        "printscreen", "prtsc", "apps", "menu",
        "f1", "f2", "f3", "f4", "f5", "f6", "f7", "f8", "f9", "f10", "f11", "f12",
        "f13", "f14", "f15", "f16", "f17", "f18", "f19", "f20", "f21", "f22", "f23", "f24",
        "a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l", "m",
        "n", "o", "p", "q", "r", "s", "t", "u", "v", "w", "x", "y", "z",
        "d0", "d1", "d2", "d3", "d4", "d5", "d6", "d7", "d8", "d9",
        "num0", "num1", "num2", "num3", "num4", "num5", "num6", "num7", "num8", "num9",
        "numpad0", "numpad1", "numpad2", "numpad3", "numpad4",
        "numpad5", "numpad6", "numpad7", "numpad8", "numpad9",
        "multiply", "add", "subtract", "separator", "decimal", "divide",
        "volumeup", "volumedown", "volumemute",
        "medianext", "mediaprev", "mediaprevious", "mediaplaypause", "mediastop",
        "browserback", "browserforward", "browserrefresh", "browserhome", "browsersearch",
    };

    private static readonly HashSet<string> Modifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "ctrl", "control", "shift", "alt", "option", "win", "windows", "cmd", "meta",
        "lctrl", "rctrl", "lshift", "rshift", "lalt", "ralt", "lwin", "rwin",
    };

    // ----------------------------------------------------------- collection

    private enum Family { Url = 0, Path = 1, Text = 2 }

    /// <summary>First-seen, budget-capped literal collector.</summary>
    private sealed class Collector
    {
        public readonly List<string> Urls = new();
        public readonly List<string> Paths = new();
        public readonly List<string> Texts = new();
        private readonly List<(Family Family, string Literal)> _order = new();
        private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

        public int Total => Urls.Count + Paths.Count + Texts.Count;

        public bool TryAdd(Family family, string literal)
        {
            if (Total >= MaxParameters)
                return false;
            if (!_seen.Add(family + "|" + Normalize(literal)))
                return false;
            _order.Add((family, literal));
            (family switch
            {
                Family.Url => Urls,
                Family.Path => Paths,
                _ => Texts,
            }).Add(literal);
            return true;
        }

        /// <summary>Assigns {{name}}s — a lone family member keeps the bare base
        /// name ("url"); with 2+ members they become url1, url2… in first-seen order.</summary>
        public NameTable FinalizeNames()
        {
            var counts = new[] { Urls.Count, Paths.Count, Texts.Count };
            var bases = new[] { "url", "path", "text" };
            var counters = new int[3];
            var table = new NameTable();

            foreach (var (family, literal) in _order)
            {
                var f = (int)family;
                counters[f]++;
                var name = counts[f] == 1 ? bases[f] : bases[f] + counters[f];
                table.Params[name] = literal;
                switch (family)
                {
                    case Family.Url: table.Urls.Add((literal, name)); break;
                    case Family.Path: table.Paths.Add((literal, name)); break;
                    case Family.Text: table.Texts[Normalize(literal)] = name; break;
                }
            }

            // Longest-first so a shorter URL/path can never partially rewrite a
            // longer one (e.g. "https://a" inside "https://a/b").
            table.Urls.Sort((x, y) => y.Literal.Length.CompareTo(x.Literal.Length));
            table.Paths.Sort((x, y) => y.Literal.Length.CompareTo(x.Literal.Length));
            return table;
        }

        private static string Normalize(string s) => s.Trim();
    }

    /// <summary>Resolved literal → placeholder tables plus the public param map.</summary>
    private sealed class NameTable
    {
        public readonly Dictionary<string, string> Params = new();
        public readonly List<(string Literal, string Name)> Urls = new();
        public readonly List<(string Literal, string Name)> Paths = new();
        /// <summary>Trimmed literal → name; text literals only rewrite whole fields.</summary>
        public readonly Dictionary<string, string> Texts = new(StringComparer.OrdinalIgnoreCase);
    }

    private static void CollectAppNames(InbriskTools.RunStep step, HashSet<string> appNames)
    {
        AddIfPresent(step.App);
        AddIfPresent(step.Search);
        AddIfPresent(step.Executable);
        AddIfPresent(step.Path);
        AddIfPresent(step.Aumid);
        if (step.Steps != null)
            foreach (var sub in step.Steps)
                if (sub != null)
                    CollectAppNames(sub, appNames);

        void AddIfPresent(string? v)
        {
            if (!string.IsNullOrWhiteSpace(v))
                appNames.Add(v.Trim());
        }
    }

    private static bool IsTextAction(string? action) =>
        string.Equals(action, "type", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(action, "set_value", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(action, "setvalue", StringComparison.OrdinalIgnoreCase);

    private static void CollectStep(InbriskTools.RunStep step, HashSet<string> appNames, Collector c)
    {
        var freeText = IsTextAction(step.Action);

        // Fixed field order → deterministic first-seen parameter ordering.
        ScanField(step.Text, c, freeText, appNames, url: true, path: true);
        ScanField(step.Value, c, freeText, appNames, url: true, path: true);
        ScanField(step.Uri, c, freeText: false, appNames, url: true, path: false);
        ScanField(step.Path, c, freeText: false, appNames, url: false, path: true);
        ScanField(step.Executable, c, freeText: false, appNames, url: false, path: true);
        if (step.Arguments != null)
            foreach (var arg in step.Arguments)
                ScanField(arg, c, freeText: false, appNames, url: true, path: true);

        if (step.Steps != null)
            foreach (var sub in step.Steps)
                if (sub != null)
                    CollectStep(sub, appNames, c);
    }

    /// <summary>URL-then-path scan of one field; falls back to free-text when the
    /// field contained no URL/path match at all and the action allows it.</summary>
    private static void ScanField(string? raw, Collector c, bool freeText,
        HashSet<string> appNames, bool url, bool path)
    {
        if (string.IsNullOrEmpty(raw) || raw.Contains("{{", StringComparison.Ordinal))
            return; // empty, or already carries a placeholder — leave it alone

        var matched = false;

        if (url)
        {
            var spans = new List<(int Start, int End)>();
            foreach (Match m in UrlRe.Matches(raw))
            {
                var literal = m.Value.TrimEnd(UrlTrailers);
                if (literal.Length == 0)
                    continue;
                matched = true;
                spans.Add((m.Index, m.Index + literal.Length));
                c.TryAdd(Family.Url, literal);
            }

            if (path)
            {
                foreach (Match m in PathRe.Matches(raw))
                {
                    // Don't re-collect a drive path embedded inside a URL match.
                    var overlapsUrl = spans.Any(s => m.Index < s.End && m.Index + m.Length > s.Start);
                    if (overlapsUrl)
                        continue;
                    matched = true;
                    c.TryAdd(Family.Path, m.Value);
                }
            }
        }
        else if (path)
        {
            foreach (Match m in PathRe.Matches(raw))
            {
                matched = true;
                c.TryAdd(Family.Path, m.Value);
            }
        }

        if (!matched && freeText && LooksLikeFreeText(raw, appNames))
            c.TryAdd(Family.Text, raw.Trim());
    }

    /// <summary>Does a type/set_value payload read as free text rather than a
    /// control sequence, key name, hotkey, bound ref, or the recipe's own app?</summary>
    private static bool LooksLikeFreeText(string raw, HashSet<string> appNames)
    {
        var t = raw.Trim();
        if (t.Length < 2)
            return false;
        if (!t.Any(char.IsLetterOrDigit))
            return false;                    // pure punctuation/whitespace
        if (t.Any(char.IsControl))
            return false;                    // embedded control characters
        if (t.Contains("{{", StringComparison.Ordinal))
            return false;                    // already parameterized
        if (t.StartsWith('$') || t.StartsWith("uia_", StringComparison.OrdinalIgnoreCase))
            return false;                    // $bound-ref / elementId look-alike
        if (appNames.Contains(t))
            return false;                    // the launch app name itself
        if (BraceSeqRe.IsMatch(t))
            return false;                    // {ENTER} {TAB}-style sequences

        // Hotkey combo: 2+ '+'-joined tokens, all key names, at least one modifier.
        var parts = t.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 2 && parts.All(KeyNames.Contains) && parts.Any(Modifiers.Contains))
            return false;

        // Lone key name: "enter", "esc", "f5", single KeyCode letters/digits.
        if (parts.Length == 1 && KeyNames.Contains(t))
            return false;

        return true;
    }

    // -------------------------------------------------------------- rewrite

    private static InbriskTools.RunStep RewriteStep(InbriskTools.RunStep step, NameTable names)
    {
        var subSteps = step.Steps;
        if (subSteps != null)
        {
            var rs = new InbriskTools.RunStep[subSteps.Length];
            for (var i = 0; i < subSteps.Length; i++)
                rs[i] = subSteps[i] == null ? null! : RewriteStep(subSteps[i], names);
            subSteps = rs;
        }

        var ifValue = step.IfValue;
        if (ifValue != null)
            ifValue = ifValue with
            {
                Contains = RewriteField(ifValue.Contains, names),
                Exact = RewriteField(ifValue.Exact, names),
            };

        return step with
        {
            Text = RewriteField(step.Text, names),
            Value = RewriteField(step.Value, names),
            Uri = RewriteField(step.Uri, names),
            Path = RewriteField(step.Path, names),
            Executable = RewriteField(step.Executable, names),
            Arguments = step.Arguments?.Select(a => RewriteField(a, names) ?? a).ToArray(),
            Contains = RewriteField(step.Contains, names),
            Exact = RewriteField(step.Exact, names),
            IfValue = ifValue,
            Steps = subSteps,
        };
    }

    /// <summary>
    /// Whole-field equality against collected text literals, then substring
    /// replacement of collected URL/path literals (case-insensitive — matches
    /// the dedupe semantics so a differently-cased re-occurrence still resolves
    /// to the same parameter).
    /// </summary>
    private static string? RewriteField(string? value, NameTable names)
    {
        if (string.IsNullOrEmpty(value) || value.Contains("{{", StringComparison.Ordinal))
            return value;

        var trimmed = value.Trim();
        if (names.Texts.TryGetValue(trimmed, out var textName))
            return "{{" + textName + "}}";

        var v = value;
        foreach (var (literal, name) in names.Urls)
            v = ReplaceLiteral(v, literal, name);
        foreach (var (literal, name) in names.Paths)
            v = ReplaceLiteral(v, literal, name);
        return v;
    }

    private static string ReplaceLiteral(string field, string literal, string name)
    {
        if (!field.Contains(literal, StringComparison.OrdinalIgnoreCase))
            return field;
        return Regex.Replace(
            field,
            Regex.Escape(literal),
            "{{" + name + "}}",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    // --------------------------------------------------------------- helper

    private static string Classify(string literal) =>
        UrlRe.Match(literal) is { Index: 0, Length: > 0 } m && m.Length == literal.Length ? "URL" :
        PathRe.Match(literal) is { Index: 0, Length: > 0 } p && p.Length == literal.Length ? "file path" :
        "text";
}
