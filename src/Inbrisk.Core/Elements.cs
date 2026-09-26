namespace Inbrisk.Core;

/// <summary>Which backend produced an element / performed an action.</summary>
public enum BackendId { Uia, Cdp, Vision, Ocr, Win32, AppAdapter }

/// <summary>Normalized cross-backend control role.</summary>
public enum Role
{
    Window, Pane, Button, Edit, Document, Menu, MenuItem, Tab, TabItem,
    List, ListItem, Tree, TreeItem, ComboBox, CheckBox, RadioButton,
    Slider, Toggle, Toolbar, Dialog, Text, Image, Hyperlink, ProgressBar,
    ScrollBar, Spinner, StatusBar, Table, DataItem, TitleBar, Separator,
    Group, Custom, Unknown,
}

/// <summary>
/// Opaque backend reference plus everything needed to find the element again
/// when it goes stale.
/// </summary>
public sealed record ReResolveRecipe(
    int? Pid,
    long? Hwnd,
    string? OwnerTitle,
    Role Role,
    string? Name,
    string? AutomationId,
    IReadOnlyList<AncestryStep> AncestryPath,
    RectPx LastBounds);

/// <summary>One step of the ancestor chain used for re-resolution (child last).</summary>
public sealed record AncestryStep(Role Role, string? Name, string? AutomationId, int IndexAmongSiblings);

public sealed record ElementHandle(
    BackendId Backend,
    string BackendRef,
    ReResolveRecipe Recipe);

public sealed record UiElement(
    string Id,
    BackendId Source,
    Role Role,
    string? Name,
    RectPx Bounds,
    IReadOnlyList<string> Actions,
    IReadOnlyDictionary<string, object?> Props,
    ElementHandle Handle,
    int? Pid,
    long? Hwnd,
    double? Confidence = null)
{
    public (int X, int Y) Center => Bounds.Center;
    public bool IsStale;
}

/// <summary>Query to locate elements. Name matching is case-insensitive contains.</summary>
public sealed record FindSpec(
    long? Hwnd = null,
    string? WindowTitle = null,
    int? Pid = null,
    Role? Role = null,
    string? Name = null,
    string? AutomationId = null,
    int MaxDepth = 12,
    int MaxResults = 50,
    /// <summary>Backend element id — search THAT element's subtree only
    /// (within:$ref). Narrowest scope; falls back to Hwnd/Pid when stale.</summary>
    string? ScopeElementId = null,
    /// <summary>Value-property substring — pushed into the native UIA
    /// condition (PropertyConditionEx MatchSubstring).</summary>
    string? ValueContains = null,
    /// <summary>Value-property exact match (case-insensitive).</summary>
    string? ValueEquals = null,
    /// <summary>ClassName-property exact match (case-insensitive).</summary>
    string? ClassName = null,
    /// <summary>IsEnabled-property match.</summary>
    bool? Enabled = null,
    /// <summary>Existence/first-match only — the backend may stop at the
    /// first native match (FindFirst) instead of enumerating the full
    /// subtree. Callers that need ambiguity detection, deterministic
    /// selection (select:nth/last) or per-candidate filters must NOT set
    /// this; it is only safe for existence checks (wait_for/ifExists).</summary>
    bool FirstOnly = false,
    /// <summary>When true, offscreen and empty-bounds elements are retained
    /// with props.offscreen=true instead of being pruned during native conversion.</summary>
    bool IncludeOffscreen = false);

public sealed record InspectOptions(
    int MaxDepth = 8,
    int MaxElements = 500,
    bool IncludeOffscreen = false);
