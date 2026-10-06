using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inbrisk.Core;

/// <summary>
/// Permanent UI map: a curated registry of known-stable element selectors for
/// standard Windows applications (notepad, calculator, explorer, taskmgr,
/// mspaint). The default table is embedded into this assembly from
/// resources/uimap.json, so it ships inside the binary — no installer change.
/// A user overlay at $INBRISK_UIMAP or &lt;data-dir&gt;/uimap.json
/// (%LOCALAPPDATA%\inbrisk by default) merges on top.
///
/// Lookups use the dotted key form "app.element" (e.g. "notepad.document");
/// "app/element" and a "map:" prefix are also accepted. Selector fields mirror
/// a semantic target: role/controlType, name/nameContains, automationId,
/// className, value. Entries flagged "verify": true are not yet confirmed
/// stable across Win10/Win11/locales — treat them as hints and fall back to
/// observation when they miss.
/// </summary>
public static class UiMap
{
    /// <summary>One element selector entry inside an app's element table.</summary>
    public sealed record Selector(
        [property: JsonPropertyName("role")] string? Role = null,
        [property: JsonPropertyName("controlType")] string? ControlType = null,
        [property: JsonPropertyName("name")] string? Name = null,
        [property: JsonPropertyName("nameContains")] string? NameContains = null,
        [property: JsonPropertyName("automationId")] string? AutomationId = null,
        [property: JsonPropertyName("className")] string? ClassName = null,
        [property: JsonPropertyName("value")] string? Value = null,
        [property: JsonPropertyName("verify")] bool Verify = false,
        [property: JsonPropertyName("comment")] string? Comment = null)
    {
        /// <summary>UIA role to resolve as — "controlType" is a JSON alias.</summary>
        public string? EffectiveRole =>
            !string.IsNullOrWhiteSpace(Role) ? Role
            : !string.IsNullOrWhiteSpace(ControlType) ? ControlType
            : null;
    }

    /// <summary>One mapped application.</summary>
    public sealed record App(
        [property: JsonPropertyName("process")] string? Process = null,
        [property: JsonPropertyName("aumid")] string? Aumid = null,
        [property: JsonPropertyName("elements")] Dictionary<string, Selector>? Elements = null);

    private sealed class MapFile
    {
        [JsonPropertyName("version")] public int Version { get; set; }
        [JsonPropertyName("apps")] public Dictionary<string, App>? Apps { get; set; }
    }

    /// <summary>Accepted key prefix on string targets, e.g. "map:notepad.document".</summary>
    public const string Prefix = "map:";

    private const string ResourceName = "inbrisk.uimap.json";
    private static readonly char[] Separators = { '.', '/' };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly Lazy<Dictionary<string, App>> _apps =
        new(LoadApps, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>All app entries (built-in + user overlay), keyed case-insensitively.</summary>
    public static IReadOnlyDictionary<string, App> Apps => _apps.Value;

    /// <summary>
    /// Split "app.element" into (appKey, logicalName). Accepts '/' as the
    /// separator and an optional "map:" prefix.
    /// </summary>
    public static bool TryParseKey(string? key, out string appKey, out string logicalName)
    {
        appKey = ""; logicalName = "";
        if (string.IsNullOrWhiteSpace(key)) return false;
        var k = key.Trim();
        if (k.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            k = k[Prefix.Length..];
        var sep = k.IndexOfAny(Separators);
        if (sep <= 0 || sep >= k.Length - 1) return false;
        appKey = k[..sep].Trim();
        logicalName = k[(sep + 1)..].Trim();
        return appKey.Length > 0 && logicalName.Length > 0;
    }

    /// <summary>Resolve "app.element" to the app entry + element selector.</summary>
    public static bool TryGet(string key, out App? app, out Selector? selector)
    {
        app = null; selector = null;
        if (!TryParseKey(key, out var appKey, out var logical)) return false;
        if (!Apps.TryGetValue(appKey, out app) || app?.Elements == null) return false;
        foreach (var (name, sel) in app.Elements)
        {
            if (string.Equals(name, logical, StringComparison.OrdinalIgnoreCase))
            {
                selector = sel;
                return true;
            }
        }
        app = null;
        return false;
    }

    /// <summary>Element-level lookup when app and logical name are already split.</summary>
    public static Selector? FindElement(string appKey, string logicalName)
        => TryGet($"{appKey}.{logicalName}", out _, out var sel) ? sel : null;

    /// <summary>Human-readable key list for error messages.</summary>
    public static string DescribeAvailable(string? appKey = null)
    {
        if (appKey != null && Apps.TryGetValue(appKey, out var app) && app.Elements is { Count: > 0 } els)
            return $"known '{appKey}' keys: [{string.Join(", ", els.Keys)}]";
        return $"known apps: [{string.Join(", ", Apps.Keys)}]";
    }

    /// <summary>Force a reload (e.g. after editing the overlay file).</summary>
    public static void Reload()
    {
        lock (_apps)
        {
            var fresh = LoadApps();
            _apps.Value.Clear();
            foreach (var (k, v) in fresh) _apps.Value[k] = v;
        }
    }

    private static Dictionary<string, App> LoadApps()
    {
        var apps = new Dictionary<string, App>(StringComparer.OrdinalIgnoreCase);

        // 1. embedded default — travels inside the binary, no installer change
        try
        {
            var asm = typeof(UiMap).Assembly;
            using var stream = asm.GetManifestResourceStream(ResourceName);
            if (stream != null)
                Merge(apps, JsonSerializer.Deserialize<MapFile>(stream, JsonOpts)?.Apps);
        }
        catch { }

        // 2. user overlays — later files win
        foreach (var path in OverlayPaths())
        {
            try
            {
                if (path == null || !File.Exists(path)) continue;
                Merge(apps, JsonSerializer.Deserialize<MapFile>(
                    File.ReadAllText(path), JsonOpts)?.Apps);
            }
            catch { }
        }
        return apps;
    }

    private static IEnumerable<string?> OverlayPaths()
    {
        yield return Environment.GetEnvironmentVariable("INBRISK_UIMAP");
        var dataDir = Environment.GetEnvironmentVariable("INBRISK_DATA_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "inbrisk");
        yield return Path.Combine(dataDir, "uimap.json");
        // dev convenience: uimap.json next to the binary
        yield return Path.Combine(AppContext.BaseDirectory, "uimap.json");
    }

    private static void Merge(Dictionary<string, App> into, Dictionary<string, App>? from)
    {
        if (from == null) return;
        foreach (var (key, app) in from)
        {
            if (app == null) continue;
            if (!into.TryGetValue(key, out var existing) || existing == null)
            {
                into[key] = app;
                continue;
            }
            var elements = existing.Elements != null
                ? new Dictionary<string, Selector>(existing.Elements, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, Selector>(StringComparer.OrdinalIgnoreCase);
            if (app.Elements != null)
                foreach (var (name, sel) in app.Elements)
                    elements[name] = sel;
            into[key] = existing with
            {
                Process = app.Process ?? existing.Process,
                Aumid = app.Aumid ?? existing.Aumid,
                Elements = elements,
            };
        }
    }
}
