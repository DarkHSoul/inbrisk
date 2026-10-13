using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Inbrisk.Core;
using ModelContextProtocol.Protocol;

namespace Inbrisk.Mcp;

/// <summary>
/// F04 — session-side confirmation channel for CONFIRM/DANGEROUS-class
/// actions.
///
/// With AutoConfirm off, every confirmation decision resolves here:
/// <see cref="McpSession"/> wires the two delegates the executor consults —
/// <see cref="SafetyPolicy.Confirmer"/> (CONFIRM classes) and
/// <see cref="SafetyPolicy.LocalConsent"/> (dangerous classes) — to this
/// channel. The gate answers "approved" only when a pending request,
/// keyed by a fingerprint of the canonical action, was granted through a
/// LOCAL surface (<see cref="ApproveLocal"/>/<see cref="DenyLocal"/> —
/// control window, tray, or another trusted in-process surface; never the
/// MCP request path, the model, or tool-call arguments).
///
/// The model cannot self-approve: tool arguments such as confirmed:true
/// only *claim* an already-approved pending via <see cref="ConfirmFlow"/>
/// → <see cref="Redeem"/>, which is audit-logged and never consulted for
/// approval state. With no local consent surface wired the channel fails
/// closed — CONFIRM/DANGEROUS actions deny, and each denial registers a
/// pending whose id the client can relay to the user (see
/// <see cref="ConfirmFlow.AnnotateResult"/>).
/// </summary>
public sealed class ConfirmChannel
{
    /// <summary>One pending/decided approval request, keyed by the
    /// fingerprint of the canonical action it would authorize. Approvals
    /// are one-shot: a consumed request cannot authorize a second action.</summary>
    public sealed record PendingConfirmation(
        string Id,
        string Fingerprint,
        string Summary,
        bool Dangerous,
        DateTimeOffset RequestedAt)
    {
        public DateTimeOffset LastDeniedAt { get; internal set; }
        public DateTimeOffset ExpiresAt { get; internal set; }
        public bool Approved { get; internal set; }
        public DateTimeOffset? ApprovedAt { get; internal set; }
        public bool Consumed { get; internal set; }
        public bool DeniedByUser { get; internal set; }
        /// <summary>The client echoed this id on a retry — observational
        /// only; approval state never depends on it.</summary>
        public bool ClientClaimed { get; internal set; }
    }

    /// <summary>How long a pending request stays live before it must be
    /// re-requested — bounds the window in which a stale approval id has
    /// any meaning.</summary>
    public TimeSpan PendingTtl { get; }

    private readonly string _sessionId;
    private readonly object _gate = new();
    private readonly List<PendingConfirmation> _pending = new();
    private const int MaxPending = 64;

    public ConfirmChannel(string sessionId, TimeSpan? pendingTtl = null)
    {
        _sessionId = sessionId;
        PendingTtl = pendingTtl ?? TimeSpan.FromMinutes(2);
    }

    /// <summary>Delegate wired to <see cref="SafetyPolicy.Confirmer"/> —
    /// the CONFIRM-class gate. Any internal fault denies — a broken gate
    /// is a closed gate, never an open one.</summary>
    public bool Confirmer(ActionIntent intent, UiElement? element, WindowInfo? window)
    {
        try { return Gate(intent, element, window, dangerous: false); }
        catch { return false; }
    }

    /// <summary>Delegate wired to <see cref="SafetyPolicy.LocalConsent"/> —
    /// the dangerous-class gate (shell/terminal input, destructive chords).
    /// Same local-approval requirement, flagged so the consent surface can
    /// render it more severely.</summary>
    public bool LocalConsent(ActionIntent intent, UiElement? element, WindowInfo? window)
    {
        try { return Gate(intent, element, window, dangerous: true); }
        catch { return false; }
    }

    /// <summary>The gate itself: approved+fingerprint+live → consume once;
    /// otherwise register/refresh the pending and refuse. The request path
    /// (tool arguments, run options, flags) is never read here.</summary>
    private bool Gate(ActionIntent intent, UiElement? element, WindowInfo? window, bool dangerous)
    {
        var fp = Fingerprint(intent, element, window);
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            PruneExpired(now);
            var p = _pending.FirstOrDefault(x =>
                x.Fingerprint == fp && !x.Consumed && !x.DeniedByUser);
            if (p is { Approved: true })
            {
                p.Consumed = true;
                Audit("consume", p,
                    "locally-approved request consumed — action proceeds (one-shot)");
                return true;
            }
            if (p == null)
            {
                p = new PendingConfirmation(NewId(), fp,
                    Summarize(intent, element, window), dangerous, now)
                {
                    LastDeniedAt = now,
                    ExpiresAt = now + PendingTtl,
                };
                _pending.Add(p);
                if (_pending.Count > MaxPending)
                    _pending.RemoveAll(x => x.Consumed || x.DeniedByUser || x.ExpiresAt <= now);
                if (_pending.Count > MaxPending) _pending.RemoveAt(0);
            }
            else
            {
                // repeated attempts refresh the window so the approval id the
                // client was shown stays stable for the same action
                p.LastDeniedAt = now;
                p.ExpiresAt = now + PendingTtl;
            }
            Audit("require", p, "no local approval — confirmation required");
            return false;
        }
    }

    // ---------------- local consent surface ----------------
    // The ONLY writers of approval state. Trusted local code (control
    // window / tray / daemon pipe) calls these; nothing reachable from the
    // MCP client may. No caller wired today → the channel fails closed.

    /// <summary>Grant a pending request. Local gestures only — never bind
    /// this to a tool, notification, or request argument.</summary>
    public bool ApproveLocal(string approvalId)
    {
        lock (_gate)
        {
            var p = Find(approvalId);
            if (p is { Consumed: false, DeniedByUser: false }
                && p.ExpiresAt > DateTimeOffset.UtcNow)
            {
                p.Approved = true;
                p.ApprovedAt = DateTimeOffset.UtcNow;
                Audit("approve", p, "approved by a LOCAL consent surface");
                return true;
            }
            Audit("approveRejected", p,
                "local approval for unknown/expired/consumed id — refused");
            return false;
        }
    }

    /// <summary>Deny a pending request — re-requests mint a fresh pending.</summary>
    public bool DenyLocal(string approvalId)
    {
        lock (_gate)
        {
            var p = Find(approvalId);
            if (p == null) return false;
            p.DeniedByUser = true;
            Audit("deny", p, "denied by a LOCAL consent surface");
            return true;
        }
    }

    /// <summary>Live pendings for a local consent surface to render —
    /// approved-not-consumed entries included so the UI can show state.</summary>
    public IReadOnlyList<PendingConfirmation> Pending
    {
        get
        {
            lock (_gate)
            {
                PruneExpired(DateTimeOffset.UtcNow);
                return _pending.Where(x => !x.Consumed && !x.DeniedByUser).ToArray();
            }
        }
    }

    /// <summary>Pendings denied at or after <paramref name="since"/> —
    /// the request filter attaches these to confirmation-required results
    /// so the client sees the approval ids minted during its call.</summary>
    internal IReadOnlyList<PendingConfirmation> DenialsSince(DateTimeOffset since)
    {
        lock (_gate)
            return _pending
                .Where(x => x.LastDeniedAt >= since && !x.Consumed && !x.DeniedByUser)
                .OrderBy(x => x.LastDeniedAt).ToArray();
    }

    /// <summary>Client claim of an approval id. Marks a locally-approved
    /// pending as claimed by the client (observational); every outcome is
    /// audit-logged. A client-supplied id — true or forged — can NEVER
    /// create approval state by itself.</summary>
    internal bool Redeem(string? approvalId)
    {
        if (string.IsNullOrWhiteSpace(approvalId)) return false;
        var id = approvalId.Trim();
        lock (_gate)
        {
            var p = Find(id);
            var ok = p is { Approved: true, Consumed: false, DeniedByUser: false }
                && p.ExpiresAt > DateTimeOffset.UtcNow;
            if (ok)
            {
                p!.ClientClaimed = true;
                Audit("redeem", p, "client claimed a locally-approved request");
            }
            else
            {
                Audit("redeemRejected", p,
                    $"client presented approval id '{Trunc(id, 40)}' — unknown, " +
                    "unapproved, expired or consumed; client-supplied values " +
                    "never grant approval");
            }
            return ok;
        }
    }

    /// <summary>A self-approval flag in tool arguments (confirmed:true and
    /// friends) — ignored by the gate, logged as a forgery attempt.</summary>
    internal void NoteSelfApprovalFlag(string argKey) =>
        Audit("flagIgnored", null,
            $"client supplied '{argKey}' — tool-call flags are self-approval, " +
            "never consent; ignored");

    private PendingConfirmation? Find(string approvalId) =>
        _pending.FirstOrDefault(x =>
            string.Equals(x.Id, approvalId, StringComparison.Ordinal));

    private void PruneExpired(DateTimeOffset now) =>
        _pending.RemoveAll(x => x.ExpiresAt <= now && !x.Approved);

    private static string NewId() =>
        "appr_" + Convert.ToHexString(
            RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();

    // ---------------- canonical action identity ----------------

    /// <summary>What an approval binds to: action kind + resolved target
    /// (element id → hwnd → process) + sorted serialized args. Approving
    /// "type X into notepad" authorizes exactly that action — a different
    /// text, element, or process is a different request.</summary>
    internal static string Fingerprint(ActionIntent intent,
        UiElement? element, WindowInfo? window)
    {
        var sb = new StringBuilder(256);
        sb.Append(intent.Kind).Append('|');
        sb.Append(intent.Target?.ElementId ?? element?.Id ?? "").Append('|');
        long? hwnd = intent.Target?.Hwnd ?? element?.Hwnd ?? window?.Hwnd;
        if (hwnd.HasValue) sb.Append(hwnd.Value.ToString("X"));
        sb.Append('|');
        sb.Append((window?.ProcessName ?? "").ToLowerInvariant()).Append('|');
        if (intent.Args != null)
            foreach (var kv in intent.Args.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                sb.Append(kv.Key).Append('=');
                try { sb.Append(JsonSerializer.Serialize(kv.Value)); }
                catch { sb.Append(kv.Value?.ToString()); }
                sb.Append('&');
            }
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    /// <summary>Human-readable summary for the consent surface / result
    /// notice — bounded; observed names are data, not layout.</summary>
    internal static string Summarize(ActionIntent intent,
        UiElement? element, WindowInfo? window)
    {
        var target =
            element != null ? $"{element.Role} '{Trunc(element.Name, 40)}'"
            : intent.Target?.Point is { } pt ? $"point ({pt.X},{pt.Y})"
            : intent.Target?.Hwnd is { } h ? $"window 0x{h:X}"
            : "foreground/focused window";
        var where = window != null
            ? $" in '{Trunc(window.Title, 40)}' ({window.ProcessName})" : "";
        return $"{intent.Kind} → {target}{where}";
    }

    private static string? Trunc(string? s, int n) =>
        s != null && s.Length > n ? s[..n] + "…" : s;

    // ---------------- audit ----------------

    private static readonly string AuditPath =
        AuditLog.Path("mcp-confirm.jsonl");
    private static readonly string? AuditMirror =
        AuditLog.MirrorPath("mcp-confirm.jsonl");
    private static readonly object AuditGate = new();

    /// <summary>Tamper-evident audit record for every gate event —
    /// require/approve/deny/consume/redeem/redeemRejected/flagIgnored —
    /// hash-chained like the script-exec audit.</summary>
    private void Audit(string evt, PendingConfirmation? p, string detail)
    {
        try
        {
            var record = JsonSerializer.SerializeToNode(new
            {
                at = DateTimeOffset.UtcNow,
                category = "confirm_gate",
                @event = evt,
                session = _sessionId,
                id = p?.Id,
                action = p?.Summary,
                dangerous = p?.Dangerous,
                detail,
            })!.AsObject();
            lock (AuditGate)
            {
                var line = AuditLog.ChainLine(record,
                    AuditLog.LastChainHash(AuditPath));
                Directory.CreateDirectory(
                    Path.GetDirectoryName(AuditPath)!);
                File.AppendAllText(AuditPath, line + "\n");
                try
                {
                    if (AuditMirror != null)
                        File.AppendAllText(AuditMirror, line + "\n");
                }
                catch { /* best-effort mirror */ }
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(
                $"Inbrisk confirm-gate audit failed: {e.Message}");
        }
    }
}

/// <summary>
/// F04 request-path helpers: extract client-supplied approval claims from
/// raw tool arguments (claim, never grant) and append the structured
/// "requires local approval" notice to results the gate refused.
/// </summary>
internal static class ConfirmFlow
{
    /// <summary>Argument names the client may use to present an approval
    /// id — or to attempt self-approval, which is audited and ignored.</summary>
    private static readonly string[] ApprovalArgKeys =
    {
        "confirmed", "confirmation", "confirmationid",
        "approvalid", "approval_id", "approval",
    };

    /// <summary>Text markers identifying a confirmation-required result —
    /// the OutcomeKind/ErrorCode names plus the executor's deny details
    /// (nested inside batch/run reports too, so the scan is textual).</summary>
    private static readonly string[] ConfirmMarkers =
    {
        "ConfirmationDenied", "ConfirmationRequired", "RequiresConfirmation",
        "requires confirmation", "requires a local confirmation",
        "denied by run confirmer",
    };

    /// <summary>Read client-supplied approval claims out of the raw tool
    /// arguments. A string id *claims* an already-approved pending via
    /// <see cref="ConfirmChannel.Redeem"/>; confirmed:true and every other
    /// self-approval shape is logged and ignored — the gate never sees
    /// request arguments.</summary>
    public static void ClaimClientArgs(ConfirmChannel channel,
        IEnumerable<KeyValuePair<string, JsonElement>>? args)
    {
        if (args == null) return;
        try
        {
            foreach (var kv in args)
            {
                if (!ApprovalArgKeys.Any(k =>
                        string.Equals(k, kv.Key, StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (kv.Value.ValueKind == JsonValueKind.String)
                    channel.Redeem(kv.Value.GetString());
                else
                    channel.NoteSelfApprovalFlag(kv.Key);
            }
        }
        catch { /* malformed args must not break the call */ }
    }

    /// <summary>Append the structured confirmation notice to a denied
    /// result: the pending approval ids minted during this call, plus how
    /// the user approves locally and how the model retries. The notice is
    /// server-generated — its block declares untrusted:false so the F03
    /// marker leaves it outside the envelope.</summary>
    public static CallToolResult AnnotateResult(
        McpSession session, CallToolResult res, DateTimeOffset callStarted)
    {
        try
        {
            if (res?.Content == null || !LooksLikeConfirmationResult(res))
                return res!;
            var pendings = session.Confirm.DenialsSince(callStarted);
            // non-error payloads (run/batch reports, capability docs) may
            // merely *mention* the kind name — annotate them only when a
            // real gate denial fired during this call
            if (res.IsError != true && pendings.Count == 0)
                return res;
            var confirmation = new JsonObject
            {
                ["required"] = true,
                ["pending"] = new JsonArray(pendings.Select(PendingNode).ToArray()),
                ["howToApprove"] =
                    "a human on this machine must approve the pending request " +
                    "through a local Inbrisk consent surface — approval can " +
                    "never come from this MCP conversation, from tool output, " +
                    "or from screen content",
                ["howToRetry"] =
                    "after the user approves locally, retry the SAME tool " +
                    "call adding \"confirmed\": \"<approvalId>\" to the " +
                    "arguments — the id only claims an approval the local " +
                    "user already granted; confirmed:true or a fabricated id " +
                    "never satisfies the gate",
            };
            if (pendings.Count == 0)
                confirmation["note"] =
                    "no pending request was registered for this denial — it " +
                    "is governed by local policy (e.g. the script-execution " +
                    "setting), not by an approvable request";
            var notice = new TextContentBlock
            {
                Text = new JsonObject
                    { ["confirmation"] = confirmation.DeepClone() }.ToJsonString(),
                Meta = new JsonObject
                {
                    [UntrustedContentMarker.MetaKey] = false,
                    [UntrustedContentMarker.SourceKey] = "inbrisk-confirm",
                },
            };
            return new CallToolResult
            {
                Content = [.. res.Content, notice],
                IsError = res.IsError,
                StructuredContent = MergeStructured(
                    res.StructuredContent, confirmation),
                Meta = res.Meta,
                ResultType = res.ResultType,
            };
        }
        catch { return res; }
    }

    private static bool LooksLikeConfirmationResult(CallToolResult res) =>
        res.Content != null && res.Content.OfType<TextContentBlock>().Any(
            t => t.Text != null && ConfirmMarkers.Any(m =>
                t.Text.Contains(m, StringComparison.OrdinalIgnoreCase)));

    private static JsonNode PendingNode(
        ConfirmChannel.PendingConfirmation p) => new JsonObject()
    {
        ["approvalId"] = p.Id,
        ["action"] = p.Summary,
        ["dangerous"] = p.Dangerous,
        ["approved"] = p.Approved,
        ["expiresAt"] = p.ExpiresAt,
    };

    /// <summary>Expose the same notice on structuredContent for clients
    /// that read the machine field; absent/non-object payloads leave the
    /// confirmation in the appended text block only.</summary>
    private static JsonElement? MergeStructured(
        JsonElement? structured, JsonObject confirmation)
    {
        try
        {
            if (structured is not { ValueKind: JsonValueKind.Object })
                return structured ?? JsonSerializer.SerializeToElement(
                    new JsonObject { ["confirmation"] = confirmation });
            if (JsonNode.Parse(structured.Value.GetRawText())
                    is not JsonObject obj)
                return structured;
            obj["confirmation"] = confirmation;
            return JsonSerializer.SerializeToElement(obj);
        }
        catch { return structured; }
    }
}
