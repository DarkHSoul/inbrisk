using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Inbrisk.Core;
using Inbrisk.Platform.Windows.Apps;
using Inbrisk.Platform.Windows.Topology;
using Inbrisk.Platform.Windows.Uia;
using Inbrisk.Runtime;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Inbrisk.Mcp;

/// <summary>
/// The MCP tool surface. Every mutating tool funnels through
/// ActionResolver — the same schema→target→frame-guard→safety→execute→
/// auto-verify pipeline the internal agent loop uses. This layer never calls
/// SendInput or UIA directly.
/// </summary>
[McpServerToolType]
public sealed partial class InbriskTools
{
    private static readonly JsonSerializerOptions J = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    private readonly McpSession _s;
    public RequestWindowSnapshot WindowSnapshot { get; }

    public InbriskTools(McpSession s)
    {
        _s = s;
        WindowSnapshot = new RequestWindowSnapshot(() => _s.Rt.Windows(), () => _s.Rt.MutationVersion);
    }

    /// <summary>Resolved output verbosity — per-call `detail` wins, then
    /// INBRISK_DETAIL env, then settings.json outputDetail, else "full".
    /// Slim exists for small-context models: every fat tool result
    /// (find/observe/run/capabilities) honours it.</summary>
    private readonly string _detailDefault =
        Environment.GetEnvironmentVariable("INBRISK_DETAIL")
        ?? Core.UserSettings.Load().OutputDetail
        ?? "slim";

    private bool Slim(string? detail) =>
        string.Equals(detail ?? _detailDefault, "slim",
            StringComparison.OrdinalIgnoreCase);

    private CallToolResult? BadDetail(string? detail) =>
        detail is null || Slim(detail) ||
        string.Equals(detail, "full", StringComparison.OrdinalIgnoreCase)
            ? null
            : Error(OutcomeKind.Malformed,
                $"detail must be \"slim\" or \"full\", got '{detail}'");

    /// <summary>Compact one-line element form for slim output — drops the
    /// always-noise fields (hwnd=0x0, repeated pid, enabled=True,
    /// actions=/bounds= keys) and pads names.</summary>
    private static string SlimEl(UiElement e,
        IReadOnlyDictionary<string, string>? tags = null)
    {
        var b = e.Bounds;
        var s = $"[{e.Id}] {e.Role}";
        if (e.Name is { Length: > 0 } n)
            s += $" \"{TruncEdges(n.Trim(), 80)}\"";
        if (e.Props.TryGetValue("value", out var v) &&
            v?.ToString() is { Length: > 0 } vv)
            s += $" value=\"{TruncEdges(vv, 80)}\"";
        if (e.Props.TryGetValue("state", out var st) && st != null)
            s += $" state={st}";
        if (e.Props.TryGetValue("labelledBy", out var lb) && lb != null)
            s += $" labelledBy=\"{lb}\"";
        s += $" ({b.X},{b.Y} {b.Width}x{b.Height})";
        if (e.Actions.Count > 0) s += $" {string.Join(",", e.Actions)}";
        if (e.Hwnd is { } h) s += $" h=0x{h:X}";
        if (e.Props.TryGetValue("enabled", out var en) && en is false)
            s += " disabled";
        if (tags != null && tags.TryGetValue(e.Id, out var tag))
            s += $" dialogRole={tag}";
        return s;
    }

    /// <summary>Compact one-line observation element — same shape as
    /// SlimEl but for ObsElement (direct Value/State fields).</summary>
    private static string SlimObsEl(ObsElement e)
    {
        var b = e.Bounds;
        var dis = e.State?.Contains("disabled", StringComparison.OrdinalIgnoreCase) == true ? " [DISABLED]" : "";
        return $"[{e.Id}] {e.Role}{dis}" +
            (e.Name is { Length: > 0 } n ? $" \"{TruncEdges(n.Trim(), 80)}\"" : "") +
            (!string.IsNullOrEmpty(e.Value)
                ? $" value=\"{TruncEdges(e.Value, 80)}\"" : "") +
            (e.State != null ? $" state={e.State}" : "") +
            $" ({b.X},{b.Y} {b.Width}x{b.Height})" +
            (e.Actions.Count > 0 ? $" {string.Join(",", e.Actions)}" : "");
    }

    /// <summary>EmergencyStopped detail — always names the resume chord so
    /// a zero-knowledge agent can tell the user how to restore control.</summary>
    private string StoppedDetail =>
        $"computer control stopped by local hotkey; only the local user can " +
        $"resume with {_s.Control.ResumeHotkey}";

    /// <summary>Schema text for the per-call desktop override. Kept as an
    /// override, not the mechanism: the session picks its desktop once via
    /// computer_session({mode}), and every tool follows it silently.</summary>
    private const string GhostOverrideDoc =
        "override the session desktop for this call only — omit to follow " +
        "computer_session({mode:\"interactive\"|\"ghost\"})";

    /// <summary>Effective ghost routing for one call: an explicit per-call
    /// value wins, otherwise the session's desktop decides.</summary>
    private bool GhostOn(bool? perCall) => _s.ResolveGhost(perCall);
}
