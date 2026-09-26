using Inbrisk.Core;

namespace Inbrisk.Runtime;

public sealed record RelationalAction(
    string ElementId,
    string Name,
    string Role,
    string Action);

public sealed record RelationalRow(
    string ElementId,
    string Role,
    string? Title,
    IReadOnlyList<string> Details,
    IReadOnlyList<RelationalAction> Actions);

public sealed record RelationalContainer(
    string ElementId,
    string Role,
    string? Name,
    IReadOnlyList<RelationalRow> Rows);

/// <summary>
/// Extracts structured relational representations from flat UI element trees.
/// Groups row items (ListItem, DataItem, etc.) with their associated primary title,
/// secondary metadata texts, and contextual action buttons (menus, play, toggle).
/// Reduces LLM disambiguation errors and token overhead.
/// </summary>
public static class RelationalInspector
{
    private static readonly HashSet<Role> RowRoles = new()
    {
        Role.ListItem, Role.DataItem, Role.TreeItem, Role.Group
    };

    private static readonly HashSet<Role> ActionRoles = new()
    {
        Role.Button, Role.MenuItem, Role.CheckBox, Role.RadioButton,
        Role.Hyperlink, Role.Toggle, Role.ComboBox
    };

    public static IReadOnlyList<RelationalRow> ExtractRows(IReadOnlyList<UiElement> elements)
    {
        if (elements == null || elements.Count == 0) return Array.Empty<RelationalRow>();

        // Identify explicit row containers
        var rows = elements.Where(e => RowRoles.Contains(e.Role)).ToList();

        // If no explicit row roles, check for elements that contain repeated interactive children
        if (rows.Count == 0)
        {
            rows = elements.Where(e => e.Role == Role.Custom || e.Role == Role.Pane).ToList();
        }

        var results = new List<RelationalRow>();

        foreach (var row in rows)
        {
            var children = elements.Where(e => e.Id != row.Id && IsDescendantOf(e, row)).ToList();
            if (children.Count == 0 && rows.Count > 1)
            {
                // Fallback: child elements bounded within row coordinates
                if (!row.Bounds.IsEmpty)
                {
                    children = elements.Where(e => e.Id != row.Id &&
                        !e.Bounds.IsEmpty &&
                        e.Bounds.X >= row.Bounds.X &&
                        e.Bounds.Y >= row.Bounds.Y &&
                        e.Bounds.Right <= row.Bounds.Right + 5 &&
                        e.Bounds.Bottom <= row.Bounds.Bottom + 5).ToList();
                }
            }

            string? title = row.Name;
            var details = new List<string>();
            var actions = new List<RelationalAction>();

            foreach (var child in children)
            {
                if (ActionRoles.Contains(child.Role) || child.Actions.Count > 0)
                {
                    var actionName = child.Actions.FirstOrDefault() ?? "click";
                    var name = !string.IsNullOrWhiteSpace(child.Name) ? child.Name : child.Role.ToString();
                    actions.Add(new RelationalAction(child.Id, name, child.Role.ToString(), actionName));
                }
                else if (child.Role is Role.Text or Role.Document || !string.IsNullOrWhiteSpace(child.Name))
                {
                    var text = child.Name ?? child.Props.GetValueOrDefault("value")?.ToString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        if (title == null || title == row.Role.ToString())
                            title = text;
                        else if (!string.Equals(title, text, StringComparison.OrdinalIgnoreCase))
                            details.Add(text);
                    }
                }
            }

            // Include row's own direct actions if any
            if (actions.Count == 0 && row.Actions.Count > 0)
            {
                actions.Add(new RelationalAction(row.Id, row.Name ?? row.Role.ToString(), row.Role.ToString(), row.Actions[0]));
            }

            results.Add(new RelationalRow(
                ElementId: row.Id,
                Role: row.Role.ToString(),
                Title: title,
                Details: details,
                Actions: actions));
        }

        return results;
    }

    public static string FormatRowsAsText(IReadOnlyList<RelationalRow> rows)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var r in rows)
        {
            sb.Append($"[{r.ElementId}] {r.Role}: ");
            if (!string.IsNullOrWhiteSpace(r.Title)) sb.Append($"\"{r.Title}\" ");
            if (r.Details.Count > 0) sb.Append($"({string.Join(" | ", r.Details)}) ");
            if (r.Actions.Count > 0)
            {
                var actStrs = r.Actions.Select(a => $"{a.Action}({a.ElementId}: \"{a.Name}\")");
                sb.Append($"actions=[{string.Join(", ", actStrs)}]");
            }
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private static bool IsDescendantOf(UiElement child, UiElement parent)
    {
        var ancestry = child.Handle?.Recipe?.AncestryPath;
        if (ancestry != null && ancestry.Count > 0)
        {
            var pAuto = parent.Handle?.Recipe?.AutomationId;
            var pName = parent.Name;
            return ancestry.Any(a =>
                (pAuto != null && a.AutomationId == pAuto) ||
                (pName != null && a.Name == pName && a.Role == parent.Role));
        }
        return false;
    }
}
