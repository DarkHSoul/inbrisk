using Inbrisk.Core;

namespace Inbrisk.Runtime;

/// <summary>
/// ALLOW / CONFIRM / DENY classification, applied centrally in the executor —
/// never inside backends. M1 policy is conservative and local.
/// </summary>
public sealed class SafetyPolicy
{
    /// <summary>Global kill switch — every action denies while set.</summary>
    public volatile bool KillSwitch;

    /// <summary>Process names (lowercase) actions may not target.</summary>
    public HashSet<string> DenyProcessNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether CONFIRM-classified actions proceed without asking.
    /// CLI sets true when running non-interactively.</summary>
    public bool AutoConfirm { get; set; } = false;

    /// <summary>Optional approval hook consulted for CONFIRM actions when
    /// AutoConfirm is false. The agent orchestrator wires its Confirmer here;
    /// null → CONFIRM actions fail with ConfirmationRequired.</summary>
    public Func<ActionIntent, UiElement?, WindowInfo?, bool>? Confirmer { get; set; }

    public SafetyClass Classify(ActionIntent intent, UiElement? element, WindowInfo? window)
    {
        if (KillSwitch) return SafetyClass.Deny;

        if (window?.IsElevated == true) return SafetyClass.Deny;
        if (element?.Props.TryGetValue("isPassword", out var p) == true && p is true)
            return intent.Kind is ActionKind.SetValue or ActionKind.TypeText
                ? SafetyClass.Deny : SafetyClass.Confirm;
        if (window?.ProcessName != null && DenyProcessNames.Contains(window.ProcessName))
            return SafetyClass.Deny;

        return intent.Kind switch
        {
            ActionKind.TypeText or ActionKind.SetValue => SafetyClass.Confirm,
            ActionKind.Hotkey or ActionKind.KeyPress => SafetyClass.Confirm,
            ActionKind.ClipboardWrite => SafetyClass.Confirm,
            ActionKind.ClipboardRead => SafetyClass.Confirm,
            _ => SafetyClass.Safe,
        };
    }
}
