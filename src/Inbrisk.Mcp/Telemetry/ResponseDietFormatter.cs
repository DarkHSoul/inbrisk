using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inbrisk.Mcp.Telemetry;

/// <summary>
/// Next action codes indicating the next step expected from the client/agent.
/// </summary>
public static class NextActionCodes
{
    public const string None = "none";
    public const string Observe = "observe";
    public const string DismissModal = "dismiss_modal";

    public static string Normalize(string? action)
    {
        if (string.IsNullOrWhiteSpace(action)) return None;
        var trimmed = action.Trim().ToLowerInvariant();
        return trimmed switch
        {
            "observe" or "inspect" => Observe,
            "dismiss_modal" or "dismissmodal" or "modal" => DismissModal,
            _ => None
        };
    }
}

/// <summary>
/// Compact slim response representation for MCP tool outputs.
/// Excludes verbose session metadata and bounding boxes by default, reducing payload
/// and token consumption for agent turns.
/// </summary>
public sealed record DietResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; } = true;

    [JsonPropertyName("st")]
    public string St { get; init; } = "Verified";

    [JsonPropertyName("ev")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Ev { get; init; }

    [JsonPropertyName("post")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Post { get; init; }

    [JsonPropertyName("changed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<object>? Changed { get; init; }

    [JsonPropertyName("new")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? New { get; init; }

    [JsonPropertyName("stateVersion")]
    public long StateVersion { get; init; }

    [JsonPropertyName("next")]
    public string Next { get; init; } = NextActionCodes.None;

    // --- Extended Debug Fields: Excluded unless debug detail is explicitly requested ---

    [JsonPropertyName("bounds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Bounds { get; init; }

    [JsonPropertyName("session")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Session { get; init; }

    [JsonPropertyName("debug")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Debug { get; init; }
}

/// <summary>
/// Enforces default compact slim responses and strips verbose session/bounds bloat
/// unless debug detail is explicitly enabled.
/// </summary>
public static class ResponseDietFormatter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly JsonSerializerOptions IndentedJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Creates a default compact slim response. Excludes bounds and session details.
    /// Format: { ok, st: "Verified", ev: "value", post: {...}, changed: [...], new: {...}, stateVersion: N, next: "none" }
    /// </summary>
    public static DietResponse CreateSlim(
        bool ok = true,
        string st = "Verified",
        object? ev = null,
        object? post = null,
        IReadOnlyList<object>? changed = null,
        object? @new = null,
        long stateVersion = 0,
        string next = NextActionCodes.None)
    {
        return new DietResponse
        {
            Ok = ok,
            St = st,
            Ev = ev,
            Post = post,
            Changed = changed,
            New = @new,
            StateVersion = stateVersion,
            Next = NextActionCodes.Normalize(next),
            Bounds = null,
            Session = null,
            Debug = null
        };
    }

    /// <summary>
    /// Creates a debug response with full bounds, session metadata, and diagnostics.
    /// </summary>
    public static DietResponse CreateDebug(
        bool ok = true,
        string st = "Verified",
        object? ev = null,
        object? post = null,
        IReadOnlyList<object>? changed = null,
        object? @new = null,
        long stateVersion = 0,
        string next = NextActionCodes.None,
        object? bounds = null,
        object? session = null,
        object? debug = null)
    {
        return new DietResponse
        {
            Ok = ok,
            St = st,
            Ev = ev,
            Post = post,
            Changed = changed,
            New = @new,
            StateVersion = stateVersion,
            Next = NextActionCodes.Normalize(next),
            Bounds = bounds,
            Session = session,
            Debug = debug
        };
    }

    /// <summary>
    /// Formats a DietResponse based on debug detail flag.
    /// When debugDetail is false, bounds/session/debug are strictly omitted.
    /// </summary>
    public static DietResponse Format(
        bool ok,
        string st,
        object? ev = null,
        object? post = null,
        IReadOnlyList<object>? changed = null,
        object? @new = null,
        long stateVersion = 0,
        string next = NextActionCodes.None,
        object? bounds = null,
        object? session = null,
        object? debug = null,
        bool debugDetail = false)
    {
        return debugDetail
            ? CreateDebug(ok, st, ev, post, changed, @new, stateVersion, next, bounds, session, debug)
            : CreateSlim(ok, st, ev, post, changed, @new, stateVersion, next);
    }

    /// <summary>
    /// Serializes a DietResponse to a compact or indented JSON string.
    /// </summary>
    public static string Serialize(DietResponse response, bool writeIndented = false)
    {
        ArgumentNullException.ThrowIfNull(response);
        return JsonSerializer.Serialize(response, writeIndented ? IndentedJsonOptions : JsonOptions);
    }

    /// <summary>
    /// Transforms an arbitrary response dictionary into the canonical diet dictionary.
    /// Strips bounds, coordinate geometries, and session metadata unless debugDetail is true.
    /// </summary>
    public static Dictionary<string, object?> EnforceDiet(
        IDictionary<string, object?> rawResponse,
        bool debugDetail = false,
        long stateVersion = 0)
    {
        ArgumentNullException.ThrowIfNull(rawResponse);

        bool ok = true;
        if (rawResponse.TryGetValue("ok", out var okVal) && okVal is bool b)
            ok = b;
        else if (rawResponse.TryGetValue("success", out var sVal) && sVal is bool sb)
            ok = sb;

        string st = "Verified";
        if (rawResponse.TryGetValue("st", out var stVal) && stVal is string stStr)
            st = stStr;
        else if (rawResponse.TryGetValue("status", out var statusVal) && statusVal is string statusStr)
            st = statusStr;

        rawResponse.TryGetValue("ev", out var ev);
        if (ev == null && rawResponse.TryGetValue("evidence", out var evAlt))
            ev = evAlt;

        rawResponse.TryGetValue("post", out var post);
        rawResponse.TryGetValue("changed", out var changed);
        rawResponse.TryGetValue("new", out var @new);

        long effectiveVersion = stateVersion;
        if (effectiveVersion == 0 && rawResponse.TryGetValue("stateVersion", out var svObj) && svObj is long sv)
            effectiveVersion = sv;
        else if (effectiveVersion == 0 && rawResponse.TryGetValue("mutationVersion", out var mvObj) && mvObj is long mv)
            effectiveVersion = mv;

        string next = NextActionCodes.None;
        if (rawResponse.TryGetValue("next", out var nextObj) && nextObj is string nextStr)
            next = NextActionCodes.Normalize(nextStr);

        var result = new Dictionary<string, object?>
        {
            ["ok"] = ok,
            ["st"] = st,
            ["stateVersion"] = effectiveVersion,
            ["next"] = next
        };

        if (ev != null) result["ev"] = ev;
        if (post != null) result["post"] = post;
        if (changed != null) result["changed"] = changed;
        if (@new != null) result["new"] = @new;

        if (debugDetail)
        {
            if (rawResponse.TryGetValue("bounds", out var bounds)) result["bounds"] = bounds;
            else if (rawResponse.TryGetValue("rect", out var rect)) result["bounds"] = rect;

            if (rawResponse.TryGetValue("session", out var session)) result["session"] = session;
            if (rawResponse.TryGetValue("debug", out var debug)) result["debug"] = debug;
        }

        return result;
    }
}
