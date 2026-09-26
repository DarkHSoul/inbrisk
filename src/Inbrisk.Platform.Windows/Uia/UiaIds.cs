using Interop.UIAutomationClient;

namespace Inbrisk.Platform.Windows.Uia;

/// <summary>Well-known UIA property / pattern / event ids (documented constants).</summary>
internal static class UiaIds
{
    // property ids
    public const int BoundingRectangleProperty = 30001;
    public const int ProcessIdProperty = 30002;
    public const int ControlTypeProperty = 30003;
    public const int LocalizedControlTypeProperty = 30004;
    public const int NameProperty = 30005;
    public const int HasKeyboardFocusProperty = 30008;
    public const int IsEnabledProperty = 30010;
    public const int AutomationIdProperty = 30011;
    public const int ClassNameProperty = 30012;
    public const int LabeledByProperty = 30018;
    public const int IsPasswordProperty = 30019;
    public const int NativeWindowHandleProperty = 30020;
    public const int IsOffscreenProperty = 30022;
    public const int IsControlElementProperty = 30095;
    public const int ValueValueProperty = 30045;
    public const int SelectionItemIsSelectedProperty = 30079;
    public const int ExpandCollapseStateProperty = 30070;
    public const int ToggleStateProperty = 30086;

    // pattern ids
    public const int InvokePattern = 10000;
    public const int SelectionPattern = 10001;
    public const int ValuePattern = 10002;
    public const int RangeValuePattern = 10003;
    public const int ScrollPattern = 10004;
    public const int ExpandCollapsePattern = 10005;
    public const int WindowPattern = 10009;
    public const int SelectionItemPattern = 10010;
    public const int TogglePattern = 10015;
    public const int ScrollItemPattern = 10017;
    public const int LegacyIAccessiblePattern = 10018;
    public const int ItemContainerPattern = 10019;
    public const int VirtualizedItemPattern = 10020;

    // event ids
    public const int WindowOpenedEvent = 20016;
    public const int WindowClosedEvent = 20017;
    public const int AutomationPropertyChangedEvent = 20004;

    /// <summary>Pattern id → advertised action names.</summary>
    internal static readonly (int Id, string Action)[] PatternActions =
    {
        (InvokePattern, "invoke"),
        (ValuePattern, "setvalue"),
        (TogglePattern, "toggle"),
        (SelectionItemPattern, "select"),
        (ExpandCollapsePattern, "expandcollapse"),
        (RangeValuePattern, "setvalue"),
        (ScrollItemPattern, "scrollintoview"),
        (LegacyIAccessiblePattern, "invoke"),
    };

    internal static string[] ActionsFor(IUIAutomation uia, IUIAutomationElement el,
        bool cached = false)
    {
        var actions = new List<string> { "click" }; // coordinate click always possible
        if (cached)
        {
            // the cache request already prefetched every pattern in
            // PatternActions — a non-null GetCachedPattern means supported,
            // with zero cross-process calls
            foreach (var (id, action) in PatternActions)
            {
                try
                {
                    UiaPerf.CachedRead();
                    if (el.GetCachedPattern(id) != null && !actions.Contains(action))
                        actions.Add(action);
                }
                catch { }
            }
            return actions.ToArray();
        }
        try
        {
            UiaPerf.LiveRead();
            uia.PollForPotentialSupportedPatterns(el, out var ids, out _);
            if (ids != null)
                foreach (var (id, action) in PatternActions)
                    if (ids.Contains(id) && !actions.Contains(action))
                        actions.Add(action);
        }
        catch { /* probing is best-effort */ }
        return actions.ToArray();
    }
}
