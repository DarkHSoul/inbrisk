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
    /// CLI sets true when running non-interactively. NOTE: never consulted
    /// for dangerous actions — <see cref="IsDangerous"/> classes bypass
    /// AutoConfirm entirely (F04).</summary>
    public bool AutoConfirm { get; set; } = false;

    /// <summary>Optional approval hook consulted for CONFIRM actions when
    /// AutoConfirm is false. The agent orchestrator wires its Confirmer here;
    /// null → CONFIRM actions fail with ConfirmationRequired. Never
    /// consulted for dangerous actions.</summary>
    public Func<ActionIntent, UiElement?, WindowInfo?, bool>? Confirmer { get; set; }

    /// <summary>Process names (lowercase) whose windows are arbitrary
    /// command channels — keystrokes typed into them are a shell, not UI
    /// automation. Mirrors the AppService launch blocklist so the agent
    /// cannot launder `cmd /c` through a focused terminal either.</summary>
    public HashSet<string> DangerousProcessNames { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "cmd.exe", "powershell", "powershell.exe", "pwsh", "pwsh.exe",
        "wt", "wt.exe", "windowsterminal", "windowsterminal.exe",
        "conhost", "conhost.exe", "wsl", "wsl.exe", "bash", "bash.exe",
        "sh", "sh.exe", "zsh", "zsh.exe", "fish", "fish.exe", "nu", "nu.exe",
        "python", "python.exe", "pythonw", "pythonw.exe", "node", "node.exe",
        "wscript", "wscript.exe", "cscript", "cscript.exe", "mshta",
        "mshta.exe", "rundll32", "rundll32.exe", "regsvr32", "regsvr32.exe",
    };

    /// <summary>Local-consent hook for DANGEROUS actions. May only be wired
    /// by a trusted local surface (control window / tray) — never by the
    /// MCP request path, the model, or a run-scoped confirmer. STUBBED in
    /// this pass: null → dangerous actions deny with ConfirmationRequired
    /// until a local approval UI wires this.</summary>
    public Func<ActionIntent, UiElement?, WindowInfo?, bool>? LocalConsent { get; set; }

    /// <summary>F04 dangerous classification: a CONFIRM-classified action
    /// whose effect is a destructive or arbitrary-execution channel —
    /// typed input into a shell/terminal host, or a window-closing chord.
    /// Dangerous actions can only be approved by <see cref="LocalConsent"/>
    /// (a local human gesture); AutoConfirm and request-scoped confirmers
    /// are forgeable by the model and are ignored.</summary>
    public bool IsDangerous(ActionIntent intent, UiElement? element, WindowInfo? window)
    {
        // keystrokes/typed text into a shell host = arbitrary command exec
        if (window?.ProcessName != null &&
            DangerousProcessNames.Contains(window.ProcessName) &&
            intent.Kind is ActionKind.TypeText or ActionKind.SetValue
                or ActionKind.KeyPress or ActionKind.Hotkey)
            return true;

        // destructive close chord (Alt+F4, Ctrl+W, Ctrl+F4, Ctrl+Q) —
        // mirrors the tool-layer IsClosingHotkey list so a direct Executor
        // caller gets the same coverage
        if (intent.Kind == ActionKind.Hotkey && IsClosingChord(intent))
            return true;

        return false;
    }

    private static bool IsClosingChord(ActionIntent intent)
    {
        if (intent.Args?.TryGetValue("key", out var k) != true || k == null)
            return false;
        var key = k.ToString()?.Trim() ?? "";
        var mods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // the chord may arrive combined in `key` ("ctrl+alt+F4") — split
        // the trailing token out as the key, everything else as modifiers
        if (key.Contains('+'))
        {
            var parts = key.Split('+',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            key = parts.LastOrDefault() ?? "";
            foreach (var p in parts.Take(parts.Length - 1)) mods.Add(p);
        }
        if (intent.Args.TryGetValue("modifiers", out var m))
        {
            if (m is string s)
                foreach (var part in s.Split('+',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    mods.Add(part);
            else if (m is System.Collections.IEnumerable items)
                foreach (var i in items)
                    foreach (var part in (i?.ToString() ?? "").Split('+',
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        mods.Add(part);
        }
        if (mods.Contains("menu")) mods.Add("alt");
        if (key.Equals("F4", StringComparison.OrdinalIgnoreCase) && mods.Contains("alt"))
            return true;
        if ((key.Equals("W", StringComparison.OrdinalIgnoreCase) ||
             key.Equals("F4", StringComparison.OrdinalIgnoreCase)) && mods.Contains("ctrl"))
            return true;
        if (key.Equals("Q", StringComparison.OrdinalIgnoreCase) && mods.Contains("ctrl"))
            return true;
        return false;
    }

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
