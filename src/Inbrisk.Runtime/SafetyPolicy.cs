using System.Collections.Concurrent;
using System.Text;
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

    /// <summary>Window ownership source, consulted only to decide whether a
    /// CONFIRM-class action can be treated as ordinary agent work.
    ///
    /// NULL is the safe default: with no provenance wired the agent-owned
    /// exemption below can never fire and every kind keeps its original
    /// CONFIRM class — i.e. an unwired policy behaves exactly like the
    /// pre-ownership one. Wired by the composition root
    /// (<c>InbriskRuntime</c>) to the same ProcessProvenanceService the
    /// launch path registers into, so a window the agent opened is
    /// recognisable as such.</summary>
    public IProcessProvenanceService? Provenance { get; set; }

    /// <summary>Whether text entry (TypeText/SetValue) into a window this
    /// agent itself opened may skip the confirmation gate.
    ///
    /// Default TRUE. The rationale is narrow: writing into a window that
    /// did not exist before this task carries none of the risks the
    /// CONFIRM class exists to prevent — there is no user data to damage
    /// and no user keystroke to steal. Every other condition still wins,
    /// and the dangerous-class check runs BEFORE this one, so a shell or
    /// terminal the agent launched itself is still arbitrary execution and
    /// still needs local consent. Set false to restore the blanket
    /// confirm-on-type behaviour.</summary>
    public bool AllowTextEntryIntoAgentOwnedWindow { get; set; } = true;

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

    // ------------------------------------------------------------------
    //  F04 server-side confirm registry — grants the client cannot mint.
    //  The executor issues a challenge when a CONFIRM action has no valid
    //  grant; a local approval surface (control window / tray) decides via
    //  ApproveConfirmation / DenyConfirmation; the retry echoes the id via
    //  args.approvalId and the grant is consumed once. Neither the MCP
    //  request path nor the model can approve — they only carry the id.
    // ------------------------------------------------------------------

    private readonly ConcurrentDictionary<string, ConfirmChallenge> _confirmChallenges = new();

    /// <summary>How long an issued challenge stays open for approval.</summary>
    public TimeSpan ConfirmChallengeTtl { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Pending (unexpired, undecided) challenges, oldest expiry
    /// first — what a local approval surface should enumerate.</summary>
    public IReadOnlyList<ConfirmChallenge> PendingConfirmations =>
        _confirmChallenges.Values
            .Where(c => c.State == ConfirmChallengeState.Pending && !c.IsExpired)
            .OrderBy(c => c.ExpiresAt).ToList();

    /// <summary>Issue (or reuse) a pending challenge for this action+target.
    /// Same action+target inside the TTL returns the same challenge so a
    /// local surface sees one row per pending decision.</summary>
    public ConfirmChallenge IssueConfirmation(ActionIntent intent,
        UiElement? element, WindowInfo? window)
    {
        var hash = ConfirmFingerprint(intent, element, window);
        var existing = _confirmChallenges.Values.FirstOrDefault(c =>
            c.ActionHash == hash && !c.IsExpired &&
            c.State is ConfirmChallengeState.Pending or ConfirmChallengeState.Approved);
        if (existing != null) return existing;
        var ch = new ConfirmChallenge(
            "cnf-" + Guid.NewGuid().ToString("N")[..16], hash,
            DescribeConfirmation(intent, element, window),
            DateTimeOffset.UtcNow + ConfirmChallengeTtl);
        _confirmChallenges[ch.ApprovalId] = ch;
        if (_confirmChallenges.Count > 256)
            foreach (var dead in _confirmChallenges
                    .Where(kv => kv.Value.IsExpired || kv.Value.State
                        is ConfirmChallengeState.Denied or ConfirmChallengeState.Consumed)
                    .Select(kv => kv.Key).ToList())
                _confirmChallenges.TryRemove(dead, out _);
        return ch;
    }

    /// <summary>Local-surface approval — only in-process code can reach
    /// this; never the MCP request path. False when the id is unknown,
    /// expired or already consumed.</summary>
    public bool ApproveConfirmation(string approvalId) =>
        SetConfirmationState(approvalId, ConfirmChallengeState.Approved);

    /// <summary>Local-surface denial — a pending challenge the user refused.</summary>
    public bool DenyConfirmation(string approvalId) =>
        SetConfirmationState(approvalId, ConfirmChallengeState.Denied);

    /// <summary>Consume an approved grant: the presented id must exist, be
    /// Approved, unexpired, and bound to THIS action+target fingerprint —
    /// then it is spent (single use).</summary>
    internal bool TryConsumeConfirmation(ActionIntent intent,
        UiElement? element, WindowInfo? window, string? approvalId)
    {
        if (approvalId == null ||
            !_confirmChallenges.TryGetValue(approvalId, out var ch))
            return false;
        lock (ch)
        {
            if (ch.State != ConfirmChallengeState.Approved || ch.IsExpired ||
                ch.ActionHash != ConfirmFingerprint(intent, element, window))
                return false;
            ch.State = ConfirmChallengeState.Consumed;
            return true;
        }
    }

    private bool SetConfirmationState(string approvalId, ConfirmChallengeState state)
    {
        if (!_confirmChallenges.TryGetValue(approvalId, out var ch)) return false;
        lock (ch)
        {
            if (ch.IsExpired || ch.State == ConfirmChallengeState.Consumed)
                return false;
            ch.State = state;
            return true;
        }
    }

    /// <summary>Stable fingerprint of the action on its resolved target.
    /// Registry element ids are per-process counters, so binding uses the
    /// element's stable identity (hwnd/automationId/role/name/bounds), the
    /// caller-supplied target, the resolved window and the full args —
    /// a materially different action or target never collides.</summary>
    private static string ConfirmFingerprint(ActionIntent intent,
        UiElement? element, WindowInfo? window)
    {
        var sb = new StringBuilder(160);
        sb.Append(intent.Kind).Append('|').Append(intent.Silent).Append('|');
        var t = intent.Target;
        sb.Append(t.ElementId).Append('|').Append(t.Hwnd).Append('|');
        if (t.Point is { } pt) sb.Append(pt.X).Append(',').Append(pt.Y);
        sb.Append('|');
        if (t.Selector is { } s)
            sb.Append(s.Hwnd).Append(';').Append(s.Pid).Append(';')
              .Append(s.WindowTitle).Append(';').Append(s.Role).Append(';')
              .Append(s.Name).Append(';').Append(s.AutomationId).Append(';')
              .Append(s.ClassName).Append(';').Append(s.ValueEquals);
        sb.Append('|');
        if (element != null)
            sb.Append(element.Hwnd).Append(';').Append(element.Pid).Append(';')
              .Append(element.Role).Append(';').Append(element.Name).Append(';')
              .Append(element.Handle.Recipe.AutomationId).Append(';')
              .Append(element.Bounds);
        sb.Append('|');
        sb.Append(window?.Hwnd).Append(';').Append(window?.ProcessName);
        if (intent.Args != null)
            foreach (var kv in intent.Args.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                if (kv.Key == "approvalId") continue;
                sb.Append('|').Append(kv.Key).Append('=');
                if (kv.Value is string vs) sb.Append(vs);
                else if (kv.Value is System.Collections.IEnumerable en)
                    sb.Append(string.Join(",",
                        en.Cast<object?>().Select(o => o?.ToString())));
                else sb.Append(kv.Value);
            }
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static string DescribeConfirmation(ActionIntent intent,
        UiElement? element, WindowInfo? window)
    {
        var target = element != null
            ? $"{element.Role} '{element.Name}'"
            : intent.Target.Point is { } pt ? $"point ({pt.X},{pt.Y})"
            : window != null ? $"window '{window.Title}'" : "focused element";
        var what = intent.Kind.ToString();
        if (intent.Args?.TryGetValue("text", out var tx) == true && tx != null)
        {
            var s = tx.ToString() ?? "";
            what += $" \"{(s.Length > 60 ? s[..60] + "…" : s)}\"";
        }
        else if (intent.Args?.TryGetValue("key", out var k) == true && k != null)
            what += $" {k}";
        var where = window != null ? $" in '{window.Title}'" : "";
        return $"{what} on {target}{where}";
    }

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

    /// <summary>
    /// The policy verdict AND why. Every branch below names its reason:
    /// the executor puts it in the client-visible message and in an
    /// Attempt row, so "why was this denied/confirmed" is answerable from
    /// the result instead of requiring the reader to re-derive it from
    /// this file. <see cref="PolicyReason"/> values are the stable tokens
    /// (tests and clients match on the enum name, not on Detail prose).
    /// </summary>
    public PolicyDecision Evaluate(ActionIntent intent, UiElement? element, WindowInfo? window)
    {
        // ---- hard denies: none of these is ever reachable through a
        //      relaxation further down, so they are evaluated first ----
        if (KillSwitch)
            return PolicyDecision.Deny(PolicyReason.KillSwitch,
                "global kill switch is latched — only the local user can clear it");

        // NOTE: a blanket "target is elevated → deny" deliberately does NOT
        // live here. The executor's integrity guard is the arbiter of the
        // UIPI relation (CheckTarget — is the TARGET above OUR integrity
        // level?), so same-integrity elevated targets (admin launching
        // another admin process) still work, while a genuine higher-than-
        // ours target is refused before this code runs. A blanket deny here
        // additionally blocked text entry into admin-launched fixtures that
        // run under an elevated shell.

        if (element?.Props.TryGetValue("isPassword", out var p) == true && p is true)
            return intent.Kind is ActionKind.SetValue or ActionKind.TypeText
                ? PolicyDecision.Deny(PolicyReason.PasswordField,
                    "target element is a password field — typed input into a " +
                    "credential field never reaches an automated path")
                : PolicyDecision.Confirm(PolicyReason.PasswordField,
                    "target element is a password field");

        if (window?.ProcessName != null && DenyProcessNames.Contains(window.ProcessName))
            return PolicyDecision.Deny(PolicyReason.DenyListedProcess,
                $"'{window.ProcessName}' is on the policy deny list");

        switch (intent.Kind)
        {
            case ActionKind.TypeText or ActionKind.SetValue:
                return ClassifyTextEntry(intent, element, window);

            case ActionKind.Hotkey or ActionKind.KeyPress:
                return PolicyDecision.Confirm(PolicyReason.KeystrokeIntoWindow,
                    DescribeTarget(window, "keystroke delivery"));

            case ActionKind.ClipboardWrite or ActionKind.ClipboardRead:
                return PolicyDecision.Confirm(PolicyReason.ClipboardTransfer,
                    intent.Kind == ActionKind.ClipboardWrite
                        ? "clipboard write — it can overwrite user-copied content"
                        : "clipboard read — it can exfiltrate user-copied content");

            default:
                return PolicyDecision.Safe(PolicyReason.NotGated,
                    $"{intent.Kind} is not a CONFIRM-class action");
        }
    }

    /// <summary>
    /// Text entry. Two things are checked before the agent-ownership
    /// exemption, and the order matters:
    ///
    /// 1. <see cref="IsDangerous"/> — typed input into a shell/terminal is
    ///    arbitrary command execution. A window the agent launched itself
    ///    changes NOTHING about that, so the dangerous class must still
    ///    come out as CONFIRM and reach the executor's LocalConsent gate.
    ///    Checking it here (rather than only inside the executor's
    ///    `cls == Confirm` branch) is what keeps that gate reachable now
    ///    that the agent-owned case can return Safe.
    /// 2. Ownership — Safe only for a window registered by
    ///    RegisterAgentLaunch for this exact handle. A reused user window
    ///    (launch method ExistingInstance) is never registered, so it stays
    ///    CONFIRM, now with a reason that says so.
    /// </summary>
    private PolicyDecision ClassifyTextEntry(ActionIntent intent, UiElement? element,
        WindowInfo? window)
    {
        if (IsDangerous(intent, element, window))
            return PolicyDecision.Confirm(PolicyReason.DangerousShellInput,
                $"'{window?.ProcessName}' is an arbitrary command channel — " +
                "typed input is command execution, agent ownership does not change that");

        var ownership = window == null
            ? StrictWindowOwnership.Unknown
            : Provenance?.GetStrictWindowOwnership(window.Hwnd) ?? StrictWindowOwnership.Unknown;

        if (ownership == StrictWindowOwnership.Agent)
            return AllowTextEntryIntoAgentOwnedWindow
                ? PolicyDecision.Safe(PolicyReason.AgentOwnedWindowTextEntry,
                    $"target window 0x{window!.Hwnd:X} \"{Trim(window.Title)}\" was " +
                    "opened by this agent in this session — it holds no user data")
                : PolicyDecision.Confirm(PolicyReason.AgentOwnedWindowTextEntry,
                    $"target window 0x{window!.Hwnd:X} \"{Trim(window.Title)}\" is " +
                    "agent-owned, but AllowTextEntryIntoAgentOwnedWindow is off — " +
                    "text entry stays confirmation-gated by configuration");

        if (ownership == StrictWindowOwnership.User)
            return PolicyDecision.Confirm(PolicyReason.UserOwnedWindowTextEntry,
                $"target window 0x{window!.Hwnd:X} \"{Trim(window.Title)}\" was " +
                "already open before this task (ownership: user) — writing here " +
                "modifies user data and needs local approval");

        return PolicyDecision.Confirm(PolicyReason.UnknownWindowTextEntry,
            window == null
                ? "no resolved target window — the text would land on whatever holds focus"
                : $"target window 0x{window.Hwnd:X} \"{Trim(window.Title)}\" has no " +
                  "agent-launch provenance — treated as user content");
    }

    private static string DescribeTarget(WindowInfo? window, string what) =>
        window == null
            ? $"{what} into an unresolved target — it lands on whatever holds focus"
            : $"{what} into 0x{window.Hwnd:X} \"{Trim(window.Title)}\"";

    private static string Trim(string? s) =>
        string.IsNullOrEmpty(s) ? "" : s.Length > 60 ? s[..60] + "…" : s;

    /// <summary>Compatibility shim — callers that only want the class.
    /// New code should use <see cref="Evaluate"/> so the reason is not
    /// thrown away.</summary>
    public SafetyClass Classify(ActionIntent intent, UiElement? element, WindowInfo? window)
        => Evaluate(intent, element, window).Class;
}

/// <summary>Why the policy classified an action. Enum names are the stable,
/// machine-readable tokens published in action results; the paired Detail
/// is human-readable prose and may be reworded freely.</summary>
public enum PolicyReason
{
    /// <summary>Not a CONFIRM-class kind — nothing gated it.</summary>
    NotGated,
    /// <summary>Deny: the global kill switch is latched.</summary>
    KillSwitch,
    /// <summary>Deny: the target window runs elevated.</summary>
    ElevatedTarget,
    /// <summary>Deny (typed input) / Confirm (other kinds): password field.</summary>
    PasswordField,
    /// <summary>Deny: the target process is on the deny list.</summary>
    DenyListedProcess,
    /// <summary>Confirm: typed input into a shell/terminal = command exec.</summary>
    DangerousShellInput,
    /// <summary>Safe: text into a window this agent opened in this session.</summary>
    AgentOwnedWindowTextEntry,
    /// <summary>Confirm: text into a window that predates the task.</summary>
    UserOwnedWindowTextEntry,
    /// <summary>Confirm: text into a window with no agent-launch provenance.</summary>
    UnknownWindowTextEntry,
    /// <summary>Confirm: hotkey/keypress delivery.</summary>
    KeystrokeIntoWindow,
    /// <summary>Confirm: clipboard read or write.</summary>
    ClipboardTransfer,
}

/// <summary>A classification plus the reason behind it.</summary>
public sealed record PolicyDecision(SafetyClass Class, PolicyReason Reason, string Detail)
{
    /// <summary>Stable token safe to embed in messages and machine payloads.</summary>
    public string Code => Reason.ToString();

    public static PolicyDecision Safe(PolicyReason reason, string detail) =>
        new(SafetyClass.Safe, reason, detail);

    public static PolicyDecision Confirm(PolicyReason reason, string detail) =>
        new(SafetyClass.Confirm, reason, detail);

    public static PolicyDecision Deny(PolicyReason reason, string detail) =>
        new(SafetyClass.Deny, reason, detail);
}
