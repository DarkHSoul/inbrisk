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

public sealed partial class InbriskTools
{
    [McpServerTool(Name = "computer_adapter"), Description(
        "Direct specialist application adapter execution (for browser work prefer the dedicated browser_* tools — " +
        "browser_browse, browser_click, browser_type, browser_evaluate, browser_content, browser_tabs — " +
        "the one-call self-healing path). Chrome DevTools Protocol / CDP for DOM, script, tabs; media controls for Spotify/VLC; " +
        "specialist app APIs, CLI tools). Bypasses coordinate clicking and executes semantic commands in <5ms.")]
    public async Task<CallToolResult> AdapterExecute(
        [Description("action to execute, e.g. navigate, click, type, evaluate, get_content, list_tabs, new_tab, close_tab, play, pause, next, volume_up, read_state — script-execution actions (evaluate/exec/eval/execute) require local consent, see browser_evaluate")] string action,
        [Description("optional preferred adapter ID: chrome_devtools | media | testapp | blender (bpy socket bridge — run action:bootstrap once to install the in-Blender bridge script)")] string? adapter = null,
        [Description("optional target application specification")] TargetSpec? target = null,
        [Description("optional arguments payload for the adapter")] Dictionary<string, object?>? args = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        // Dangerous-action gate: adapter script-exec actions (CDP
        // Runtime.evaluate, Blender bpy exec/eval) run caller-supplied
        // code — the same class as browser_evaluate.
        var act = action ?? "";
        var adapterIsScriptExec = ScriptExecActions.Contains(act);
        var adapterScriptCode = adapterIsScriptExec ? ExtractScriptArg(args) : null;
        if (adapterIsScriptExec && !ScriptExecAllowed())
        {
            AuditScriptExec("computer_adapter", act, adapter,
                ok: false, reason: "denied — allowScriptExecution off",
                adapterScriptCode);
            return Error(OutcomeKind.ConfirmationDenied,
                ScriptExecDeniedDetail("computer_adapter"), sw);
        }

        // F11: adapter actions mutate apps/desktop (media control, CDP DOM
        // actions, Blender bridge) — honour the latched emergency stop.
        var epoch = _s.Control.ActionToken();
        if (epoch == null)
            return Error(OutcomeKind.EmergencyStopped, StoppedDetail, sw);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            ct, _s.SessionCts.Token, epoch.Value);
        ct = linked.Token;

        CallToolResult result;
        Inbrisk.Core.AdapterResult? res = null;
        var processName = target?.Process ?? _s?.Rt?.ForegroundWindow()?.ProcessName;
        var hwnd = target?.Hwnd != null ? ParseHwnd(target.Hwnd) : _s?.Rt?.ForegroundWindow()?.Hwnd;

        TargetRef? targetRef = null;
        if (hwnd.HasValue) targetRef = TargetRef.Window(hwnd.Value);
        else if (!string.IsNullOrEmpty(target?.ElementId)) targetRef = TargetRef.Element(target.ElementId);

        var registry = _s?.Adapters ?? new Inbrisk.Runtime.Adapters.ApplicationAdapterRegistry();
        bool handled;
        (handled, res) = await registry.TryExecuteAsync(
            action: act,
            target: targetRef,
            args: args,
            processName: processName,
            hwnd: hwnd,
            preferredAdapterId: adapter,
            ct: ct);

        if (!handled || res == null)
            result = Error(OutcomeKind.Malformed, $"no adapter available to handle action '{act}' for process '{processName}'", sw);
        else if (!res.Success)
            result = Error(res.Error == ErrorCode.NotFound ? OutcomeKind.TargetNotFound : OutcomeKind.Failed, res.Detail ?? "adapter execution failed", sw);
        else
            result = Json(new
            {
                success = true,
                method = res.Method,
                detail = res.Detail,
                data = res.Data,
                durationMs = sw.ElapsedMilliseconds,
                provenance = Provenance("adapter")
            });

        if (adapterIsScriptExec)
            AuditScriptExec("computer_adapter", act,
                adapter ?? res?.Method,
                ok: result.IsError != true,
                reason: result.IsError == true
                    ? "allowed — call did not succeed"
                    : "allowed — executed",
                adapterScriptCode);
        return result;
    }

    // ---------------- browser_* — first-class Chrome/CDP tools ----------------
    // One-call, self-healing paths over the chrome_devtools adapter: no
    // computer_launch needed — a dead CDP port auto-spawns a debug browser.

    // ---- script-execution gate (dangerous action, F04 class) ----
    // browser_evaluate and adapter script-exec actions (CDP
    // Runtime.evaluate, Blender bpy exec/eval) run caller-supplied code —
    // the same dangerous class as shell-host typing: AutoConfirm and
    // tool-call flags are self-approval, never consent. The only consent
    // channels are local and unreachable from the MCP request path:
    //   1. "AllowScriptExecution": true in settings.json
    //      (%LOCALAPPDATA%\inbrisk\settings.json, or $INBRISK_DATA_DIR),
    //      written by the setup app or edited by the user;
    //   2. INBRISK_ALLOW_SCRIPT_EXECUTION on the host process — set, it
    //      wins both ways (truthy allows; set-but-falsy force-denies even
    //      when the setting is on).
    // Every attempt — allowed or denied — is appended to the
    // tamper-evident audit log (mcp-scriptexec.jsonl, hash-chained).

    private static readonly string ScriptExecAuditPath =
        AuditLog.Path("mcp-scriptexec.jsonl");

    private static readonly string? ScriptExecAuditMirror =
        AuditLog.MirrorPath("mcp-scriptexec.jsonl");

    private static readonly object ScriptExecAuditGate = new();

    /// <summary>Adapter action names that execute caller-supplied code:
    /// chrome_devtools "evaluate" (JS) and the Blender bpy bridge
    /// "execute"/"exec"/"eval"/"evaluate" (Python). Navigation, click,
    /// type, and read actions evaluate fixed internal scripts — caller
    /// data goes in serialized, never as code — and stay open.</summary>
    private static readonly HashSet<string> ScriptExecActions =
        new(StringComparer.OrdinalIgnoreCase)
        { "evaluate", "eval", "exec", "execute" };

    /// <summary>Local-consent check for script execution. The env var
    /// wins both ways — set, its truthiness decides (deployments can
    /// force-deny); unset falls through to the settings.json flag.</summary>
    private static bool ScriptExecAllowed()
    {
        var env = Environment.GetEnvironmentVariable(
            "INBRISK_ALLOW_SCRIPT_EXECUTION")?.Trim();
        if (!string.IsNullOrEmpty(env))
            return env.Equals("1", StringComparison.OrdinalIgnoreCase)
                || env.Equals("true", StringComparison.OrdinalIgnoreCase)
                || env.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || env.Equals("on", StringComparison.OrdinalIgnoreCase);
        return Core.UserSettings.LoadCached().AllowScriptExecution;
    }

    private static string ScriptExecDeniedDetail(string surface) =>
        $"{surface} denied — it executes caller-supplied code and is a " +
        "dangerous action: consent must come from a LOCAL channel the " +
        "model cannot forge. Set \"AllowScriptExecution\": true in " +
        "settings.json (via the setup app or a manual edit) or launch the " +
        "host with INBRISK_ALLOW_SCRIPT_EXECUTION=1. AutoConfirm and " +
        "tool-call flags are ignored for dangerous classes.";

    /// <summary>Extract the caller-supplied code payload from adapter
    /// args for the audit hash — chrome uses "expression"/"script",
    /// blender "code"/"script"/"expression".</summary>
    private static string? ExtractScriptArg(
        IReadOnlyDictionary<string, object?>? args)
    {
        if (args == null) return null;
        foreach (var k in new[] { "expression", "code", "script" })
            if (args.TryGetValue(k, out var v) && v != null)
                return v.ToString();
        return null;
    }

    /// <summary>Tamper-evident audit record for every script-exec attempt —
    /// {at, tool, action, adapter, ok, reason, codeHash, codeLength}. The
    /// code payload is hashed, never stored verbatim.</summary>
    private static void AuditScriptExec(string tool, string action,
        string? adapter, bool ok, string reason, string? code)
    {
        try
        {
            var record = JsonSerializer.SerializeToNode(new
            {
                at = DateTimeOffset.UtcNow,
                category = "script_execution",
                tool,
                action,
                adapter,
                ok,
                reason,
                codeHash = code != null
                    ? Convert.ToHexString(System.Security.Cryptography.SHA256
                        .HashData(Encoding.UTF8.GetBytes(code))).ToLowerInvariant()
                    : null,
                codeLength = code?.Length,
            })!.AsObject();
            lock (ScriptExecAuditGate)
            {
                var line = AuditLog.ChainLine(record,
                    AuditLog.LastChainHash(ScriptExecAuditPath));
                Directory.CreateDirectory(
                    Path.GetDirectoryName(ScriptExecAuditPath)!);
                File.AppendAllText(ScriptExecAuditPath, line + "\n");
                try
                {
                    if (ScriptExecAuditMirror != null)
                        File.AppendAllText(ScriptExecAuditMirror, line + "\n");
                }
                catch { /* best-effort mirror */ }
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(
                $"Inbrisk script-exec audit failed: {e.Message}");
        }
    }

    /// <summary>Shared dispatch for the browser_* family — every call goes
    /// through the emergency-stop token, the script-execution gate (for
    /// caller-supplied-code actions), and the chrome_devtools adapter.</summary>
    private async Task<CallToolResult> BrowserCall(
        string action, Dictionary<string, object?> args, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // Dangerous-action gate: script-exec actions (CDP Runtime.evaluate)
        // run caller-supplied code — deny unless locally consented.
        var isScriptExec = ScriptExecActions.Contains(action);
        var scriptCode = isScriptExec ? ExtractScriptArg(args) : null;
        if (isScriptExec && !ScriptExecAllowed())
        {
            AuditScriptExec($"browser_{action}", action, "chrome_devtools",
                ok: false, reason: "denied — allowScriptExecution off",
                scriptCode);
            return Error(OutcomeKind.ConfirmationDenied,
                ScriptExecDeniedDetail($"browser_{action}"), sw);
        }

        CallToolResult result;
        var epoch = _s.Control.ActionToken();
        if (epoch == null)
        {
            result = Error(OutcomeKind.EmergencyStopped, StoppedDetail, sw);
        }
        else
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                ct, _s.SessionCts.Token, epoch.Value);

            var registry = _s?.Adapters ?? new Inbrisk.Runtime.Adapters.ApplicationAdapterRegistry();
            (bool handled, Inbrisk.Core.AdapterResult? res) = await registry.TryExecuteAsync(
                action: action,
                target: null,
                args: args,
                processName: "chrome",
                hwnd: null,
                preferredAdapterId: "chrome_devtools",
                ct: linked.Token);

            if (!handled || res == null)
                result = Error(OutcomeKind.Failed,
                    "chrome_devtools adapter unavailable", sw);
            else if (!res.Success)
                result = Error(res.Error == ErrorCode.NotFound
                        ? OutcomeKind.TargetNotFound : OutcomeKind.Failed,
                    res.Detail ?? $"browser {action} failed", sw);
            else
                result = Json(new
                {
                    success = true,
                    method = res.Method,
                    detail = res.Detail,
                    data = res.Data,
                    durationMs = sw.ElapsedMilliseconds,
                    provenance = Provenance("cdp")
                });
        }

        if (isScriptExec)
            AuditScriptExec($"browser_{action}", action, "chrome_devtools",
                ok: result.IsError != true,
                reason: result.IsError == true
                    ? "allowed — call did not succeed"
                    : "allowed — executed",
                scriptCode);
        return result;
    }

    [McpServerTool(Name = "browser_browse"), Description(
        "ONE-CALL browser navigation — ALWAYS prefer this for \"open/navigate " +
        "to a page in Chrome\" requests instead of chaining computer_launch + " +
        "computer_adapter. Attaches to a debug-enabled Chromium browser on the " +
        "CDP port (default 9222); if none is reachable it auto-spawns one " +
        "with its own profile — no separate launch step, no setup. Bare hosts " +
        "like \"google.com\" get https:// automatically. Returns the live tab " +
        "(tabId/url/title) AND the page's text content — a \"go read X\" task " +
        "is ONE call, no browser_content follow-up needed (pass " +
        "includeContent via args to skip). Pass tabId to the other browser_* " +
        "tools to pin the same tab.")]
    public Task<CallToolResult> BrowserBrowse(
        [Description("URL to open — \"google.com\" becomes https://google.com")] string url,
        [Description("open in a new tab instead of navigating the active tab")] bool newTab = false,
        [Description("CDP port — default 9222")] int? port = null,
        [Description("pin a specific tab from browser_tabs")] string? tabId = null,
        CancellationToken ct = default)
    {        if (string.IsNullOrWhiteSpace(url))
            return Task.FromResult(Error(OutcomeKind.Malformed, "url is required"));
        if (!url.Contains("://", StringComparison.Ordinal))
            url = "https://" + url.Trim();

        var args = new Dictionary<string, object?>
        {
            ["port"] = port ?? 9222,
            ["url"] = url,
        };
        if (tabId != null) args["tabId"] = tabId;
        return BrowserCall(newTab ? "new_tab" : "navigate", args, ct);
    }

    [McpServerTool(Name = "browser_click"), Description(
        "Click a DOM element in the debug browser (CDP, sub-second, no " +
        "coordinate guessing). Two ways to target: uid from browser_snapshot " +
        "(most reliable), or a CSS selector — 'a:has-text(\"Sign in\")' also " +
        "works. Prefer this over computer_click inside web pages.")]
    public Task<CallToolResult> BrowserClick(
        [Description("CSS selector, e.g. \"#search-btn\", \"button[name='q']\" — omit when using uid")] string? selector = null,
        [Description("element uid from browser_snapshot — the reliable path: no selector guessing")] int? uid = null,
        [Description("pin a specific tab from browser_tabs/browser_browse")] string? tabId = null,
        [Description("CDP port — default 9222")] int? port = null,
        CancellationToken ct = default)
        => BrowserCall("click", new Dictionary<string, object?>
        {
            ["port"] = port ?? 9222,
            ["selector"] = selector ?? "",
            ["uid"] = uid,
            ["tabId"] = tabId,
        }, ct);

    [McpServerTool(Name = "browser_type"), Description(
        "Fill an input/textarea by CSS selector in the debug browser — sets " +
        "value and fires input+change events (CDP, no keystrokes). Prefer this " +
        "over computer_type for web forms.")]
    public Task<CallToolResult> BrowserType(
        [Description("CSS selector of the input/textarea")] string selector,
        [Description("text to fill in")] string text,
        [Description("pin a specific tab from browser_tabs/browser_browse")] string? tabId = null,
        [Description("CDP port — default 9222")] int? port = null,
        CancellationToken ct = default)
        => BrowserCall("type", new Dictionary<string, object?>
        {
            ["port"] = port ?? 9222,
            ["selector"] = selector,
            ["text"] = text,
            ["tabId"] = tabId,
        }, ct);

    [McpServerTool(Name = "browser_evaluate"), Description(
        "Run JavaScript in the active page of the debug browser and return " +
        "the result (CDP Runtime.evaluate, awaitPromise + returnByValue). " +
        "The escape hatch for anything the other browser_* tools don't cover. " +
        "DANGEROUS ACTION — denied by default: requires LOCAL consent the " +
        "model cannot supply (settings.json \"AllowScriptExecution\": true, " +
        "or host env INBRISK_ALLOW_SCRIPT_EXECUTION=1). Every attempt is " +
        "audit-logged.")]
    public Task<CallToolResult> BrowserEvaluate(
        [Description("JavaScript expression to evaluate")] string expression,
        [Description("pin a specific tab from browser_tabs/browser_browse")] string? tabId = null,
        [Description("CDP port — default 9222")] int? port = null,
        CancellationToken ct = default)
        => BrowserCall("evaluate", new Dictionary<string, object?>
        {
            ["port"] = port ?? 9222,
            ["expression"] = expression,
            ["tabId"] = tabId,
        }, ct);

    [McpServerTool(Name = "browser_content"), Description(
        "Get the page's text/HTML (optionally scoped to a CSS selector) from " +
        "the debug browser — fast DOM read over CDP, no screenshots or UIA.")]
    public Task<CallToolResult> BrowserContent(
        [Description("optional CSS selector to scope extraction")] string? selector = null,
        [Description("text (default) | html")] string? format = null,
        [Description("pin a specific tab from browser_tabs/browser_browse")] string? tabId = null,
        [Description("CDP port — default 9222")] int? port = null,
        CancellationToken ct = default)
        => BrowserCall(format == "html" ? "get_html" : "get_text",
            new Dictionary<string, object?>
            {
                ["port"] = port ?? 9222,
                ["selector"] = selector,
                ["tabId"] = tabId,
            }, ct);

    [McpServerTool(Name = "browser_tabs"), Description(
        "List tabs/targets of the debug browser (self-heals like the other " +
        "browser_* tools). Returns tabId values usable in browser_browse, " +
        "browser_click, browser_type, browser_evaluate, browser_content.")]
    public Task<CallToolResult> BrowserTabs(
        [Description("CDP port — default 9222")] int? port = null,
        CancellationToken ct = default)
        => BrowserCall("list_tabs", new Dictionary<string, object?>
        {
            ["port"] = port ?? 9222,
        }, ct);

    [McpServerTool(Name = "browser_screenshot"), Description(
        "Capture the active page of the debug browser as PNG (base64) via CDP.")]
    public Task<CallToolResult> BrowserScreenshot(
        [Description("pin a specific tab from browser_tabs/browser_browse")] string? tabId = null,
        [Description("CDP port — default 9222")] int? port = null,
        CancellationToken ct = default)
        => BrowserCall("screenshot", new Dictionary<string, object?>
        {
            ["port"] = port ?? 9222,
            ["tabId"] = tabId,
        }, ct);

    [McpServerTool(Name = "browser_capture"), Description(
        "ONE-CALL DevTools-style inspection of the debug browser page: reloads " +
        "(or navigates) while collecting ALL network requests (count, per-type " +
        "breakdown, largest transfers), console messages and exceptions, then " +
        "returns runtime vitals (title/href/DOM+link+script counts, resource " +
        "count, render-blocking count, TTFB/DCL/load, CLS, LCP when available). " +
        "Use this instead of hand-rolling CDP scripts for 'analyze network/" +
        "console/performance of this page' tasks.")]
    public Task<CallToolResult> BrowserCapture(
        [Description("optional URL to navigate to first; otherwise reloads the tab")] string? url = null,
        [Description("reload the page during capture — default true, ignored when url is set")] bool reload = true,
        [Description("bypass HTTP cache on reload — default true")] bool ignoreCache = true,
        [Description("how long to collect events in ms — default 6000, clamped 500..60000")] int? durationMs = null,
        [Description("pin a specific tab from browser_tabs/browser_browse")] string? tabId = null,
        [Description("CDP port — default 9222")] int? port = null,
        CancellationToken ct = default)
        => BrowserCall("capture", new Dictionary<string, object?>
        {
            ["port"] = port ?? 9222,
            ["url"] = url ?? "",
            ["reload"] = reload,
            ["ignoreCache"] = ignoreCache,
            ["durationMs"] = durationMs,
            ["tabId"] = tabId,
        }, ct);

    [McpServerTool(Name = "browser_snapshot"), Description(
        "Take an accessibility-tree snapshot of the debug-browser page (like " +
        "chrome-devtools-mcp take_snapshot): returns elements as " +
        "uid=N role \"name\" — pass uid to browser_click(uid=..) for reliable " +
        "interaction without CSS-selector guessing. Snapshot instead of " +
        "screenshot whenever text/structure is enough. Uids reset on " +
        "navigation — re-snapshot after page changes.")]
    public Task<CallToolResult> BrowserSnapshot(
        [Description("pin a specific tab from browser_tabs/browser_browse")] string? tabId = null,
        [Description("CDP port — default 9222")] int? port = null,
        CancellationToken ct = default)
        => BrowserCall("snapshot", new Dictionary<string, object?>
        {
            ["port"] = port ?? 9222,
            ["tabId"] = tabId,
        }, ct);

    [McpServerTool(Name = "computer_list_adapters"), Description(
        "List all active specialist application adapters and their supported semantic actions.")]
    public Task<CallToolResult> ListAdapters()
    {
        var registry = _s?.Adapters ?? new Inbrisk.Runtime.Adapters.ApplicationAdapterRegistry();
        var caps = registry.GetCapabilities();
        var sb = new StringBuilder();
        sb.AppendLine($"Active Application Adapters ({caps.Count}):");
        foreach (var c in caps)
        {
            sb.AppendLine($"• [{c.AdapterId}] {c.DisplayName}");
            sb.AppendLine($"  Supported actions: {string.Join(", ", c.SupportedActions)}");
        }
        return Task.FromResult(Text(sb.ToString().TrimEnd()));
    }

    [McpServerTool(Name = "computer_leases_status"), Description(
        "Query current desktop concurrency leases: active physical input owner, concurrent read-only observers, and active tasks.")]
    public CallToolResult LeasesStatus()
    {
        var arbiter = _s?.Arbiter ?? DesktopArbiter.Shared;
        var leases = arbiter.GetActiveLeases();
        var owner = arbiter.GetExclusiveOwner();
        return Json(new
        {
            hasExclusiveOwner = owner != null,
            exclusiveOwner = owner,
            activeLeasesCount = leases.Count,
            leases = leases
        });
    }

    [McpServerTool(Name = "computer_toolset"), Description(
        "Inspect, enable, pin, or disable dynamic MCP toolsets (e.g. 'browser'). " +
        "Pinned toolsets remain visible across foreground focus changes.")]
    public CallToolResult Toolset(
        [Description("toolset name to enable and pin (e.g. 'browser')")] string? enable = null,
        [Description("toolset name to disable or unpin (e.g. 'browser')")] string? disable = null,
        [Description("optional detail verbosity: 'slim' or 'full'")] string? detail = null)
    {
        var sw = Stopwatch.StartNew();
        if (BadDetail(detail) is { } bad) return bad;

        var dt = _s.DynamicToolset;
        if (!string.IsNullOrWhiteSpace(enable))
        {
            dt.PinToolset(enable.Trim());
        }
        if (!string.IsNullOrWhiteSpace(disable))
        {
            dt.UnpinToolset(disable.Trim());
        }

        var visible = dt.GetVisibleToolNames();
        var pinned = dt.PinnedToolsets;
        var active = dt.ActiveToolsets;

        if (Slim(detail))
        {
            return Text($"toolset: pinned=[{string.Join(",", pinned)}] active=[{string.Join(",", active)}] visibleCount={visible.Count}");
        }

        return Json(new
        {
            pinned = pinned.ToArray(),
            active = active.ToArray(),
            visible = visible.ToArray(),
            compatibilityMode = dt.CompatibilityMode,
            elapsedMs = sw.ElapsedMilliseconds
        });
    }

    [McpServerTool(Name = "computer_session"), Description(
        "Read or switch the desktop this session drives. mode:\"ghost\" routes the " +
        "ghost-capable computer_* tools to the isolated background desktop " +
        "(Session 2 / PiP) — no focus theft, no physical cursor movement; " +
        "mode:\"interactive\" (the default) drives the user's live desktop. Omit " +
        "mode to just read the current one. The mode is honored by " +
        "computer_observe, computer_windows, computer_launch, computer_inspect, " +
        "computer_click, computer_type, computer_do, computer_batch and " +
        "computer_screenshot; every other tool (computer_run, computer_hotkey, " +
        "computer_key, computer_find, computer_read, computer_scroll, " +
        "computer_drag, browser_*) still acts on the interactive desktop — " +
        "compose those steps into computer_batch or computer_do, which are routed. " +
        "Switching invalidates desktop-scoped state (element ids, frame ids, " +
        "find/inspect snapshots, scope window, paused runs) and releases held input, " +
        "so re-observe after switching. Switching does NOT start the ghost desktop — " +
        "the returned ghostSession block reports whether it is actually reachable.")]
    public async Task<CallToolResult> Session(
        [Description("interactive|ghost — target desktop for the rest of the session; omit to read the current mode")] string? mode = null,
        CancellationToken ct = default)
    {
        var previous = _s.Desktop;
        if (!string.IsNullOrWhiteSpace(mode))
        {
            if (!SessionDesktops.TryParse(mode, out var target))
                return Error(OutcomeKind.Malformed,
                    $"unknown session desktop '{mode}' — use \"interactive\" or \"ghost\"");
            _s.SetDesktop(target);
        }

        var report = new Dictionary<string, object?>
        {
            ["sessionId"] = _s.SessionId,
            ["desktop"] = SessionDesktops.ToWire(_s.Desktop),
            ["changed"] = _s.Desktop != previous,
            ["previous"] = SessionDesktops.ToWire(previous),
        };
        if (_s.OnGhostDesktop)
        {
            // Reachability is the one thing the caller cannot infer: the
            // switch only routes, it never starts the background session.
            try
            {
                var status = await _s.GhostBridge.GetSessionStatusAsync(ct).ConfigureAwait(false);
                report["ghostSession"] = new
                {
                    active = status.IsActive,
                    state = status.State,
                    sessionId = status.SessionId,
                    uptimeSeconds = (long)status.Uptime.TotalSeconds,
                };
            }
            catch (Exception ex)
            {
                report["ghostSession"] = new { active = false, state = "Unreachable", error = ex.Message };
            }
        }
        return Text(JsonSerializer.Serialize(report));
    }

    [McpServerTool(Name = "computer_capabilities"), Description(
        "Machine-readable Inbrisk reference: every tool's contract, the " +
        "computer_run step schema with per-action fields and aliases, the " +
        "target spec (including labelledBy/nearText/within/ancestor), " +
        "coordinate rules, error kinds, and current control state. Call " +
        "once when unsure about syntax — not needed every task. " +
        "detail:\"slim\" returns a compact cheat-sheet (~1.5KB) instead.")]
    public CallToolResult Capabilities(
        [Description("output verbosity: slim|full — default from INBRISK_DETAIL or settings.json outputDetail")] string? detail = null,
        CancellationToken ct = default)
    {
        if (BadDetail(detail) is { } bd) return bd;
        return Text(Slim(detail)
            ? InbriskResources.CapabilitiesJsonSlim(_s.Rt.Activity, _s.Desktop, _s.Control)
            : InbriskResources.CapabilitiesJson(_s.Rt.Activity, _s.Desktop, _s.Control));
    }

    // ------------------------------------------------------------ Desktop Shell & UI

    [McpServerTool(Name = "computer_ui_status"), Description(
        "Read-only query of the Inbrisk Desktop Shell presentation state: " +
        "HUD enabled/visible, perimeter indicator, current activity, and active sessions.")]
    public CallToolResult UiStatus(CancellationToken ct = default)
    {
        var settings = UserSettings.Load();
        var hud = _s.Rt.Hud;
        var perimeter = _s.Rt.Indicator;
        var activity = _s.Rt.Activity;

        var status = new
        {
            hudEnabled = settings.HudEnabled,
            perimeterEnabled = settings.PerimeterEnabled,
            hudVisible = hud.State != HudState.Hidden && settings.HudEnabled,
            currentHudState = hud.State.ToString(),
            currentActivity = hud.CurrentText,
            activeMcpSessions = activity.ActiveClients
        };

        return Text(JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true }));
    }

    [McpServerTool(Name = "computer_ui_set"), Description(
        "Idempotently update user presentation preferences (HUD / perimeter smoke indicator). " +
        "Does not interrupt computer control, emergency hotkeys, or MCP sessions.")]
    public CallToolResult UiSet(
        [Description("Enable or disable the Floating Activity Pill HUD")] bool? hud = null,
        [Description("Enable or disable the perimeter smoke indicator")] bool? perimeter = null,
        CancellationToken ct = default)
    {
        var settings = UserSettings.Load();
        if (hud.HasValue)
        {
            settings.HudEnabled = hud.Value;
            _s.Rt.SetHudEnabled(hud.Value);
        }
        if (perimeter.HasValue)
        {
            settings.PerimeterEnabled = perimeter.Value;
        }
        settings.Save();

        var res = new
        {
            success = true,
            hudEnabled = settings.HudEnabled,
            perimeterEnabled = settings.PerimeterEnabled
        };
        return Text(JsonSerializer.Serialize(res));
    }

    [McpServerTool(Name = "computer_ui_open_settings"), Description(
        "Open the Inbrisk Settings & Tools window for the user. Interactive, non-blocking.")]
    public CallToolResult UiOpenSettings(CancellationToken ct = default)
    {
        // F11: spawning a process is a desktop mutation — honour the
        // latched emergency stop like every other mutating tool.
        if (_s.Control.ActionToken() == null)
            return Error(OutcomeKind.EmergencyStopped, StoppedDetail);

        try
        {
            var exe = Inbrisk.Setup.InstallLayout.CanonicalExePath;
            if (!File.Exists(exe))
                exe = Process.GetCurrentProcess().MainModule?.FileName ?? "inbrisk.exe";

            Process.Start(new ProcessStartInfo(exe, "settings") { UseShellExecute = true });
            return Text(JsonSerializer.Serialize(new { success = true, opened = true }));
        }
        catch (Exception ex)
        {
            return Error(OutcomeKind.Failed, $"failed to open settings window: {ex.Message}");
        }
    }

    // ------------------------------------------------------------ Application Lifecycle

    [McpServerTool(Name = "computer_app_status"), Description(
        "Read-only status of the Inbrisk SERVER itself (this tool takes no " +
        "target — it does NOT report on user apps like notepad): version, " +
        "active MCP sessions, emergency state, UI processes, install mode. " +
        "For user apps use computer_windows or computer_apps.")]
    public CallToolResult AppStatus(CancellationToken ct = default)
    {
        var status = new
        {
            running = true,
            version = Inbrisk.Setup.InstallLayout.Version,
            mcpSessions = _s.Rt.Activity.ActiveClients,
            emergencyState = _s.Control.State.ToString(),
            uiProcess = "in-process",
            installMode = Inbrisk.Setup.InstallLayout.PathsEqual(Inbrisk.Setup.InstallLayout.ProcessDir, Inbrisk.Setup.InstallLayout.UserInstallDir)
                ? "user"
                : Inbrisk.Setup.InstallLayout.PathsEqual(Inbrisk.Setup.InstallLayout.ProcessDir, Inbrisk.Setup.InstallLayout.MachineInstallDir)
                    ? "machine"
                    : "portable"
        };
        return Text(JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true }));
    }

    [McpServerTool(Name = "computer_app_restart"), Description(
        "Restart an application (or the Inbrisk SERVER itself if server:true is passed). " +
        "When process or app is specified, closes that app and relaunches it. " +
        "To restart the Inbrisk server itself, server:true must be explicitly passed.")]
    public async Task<CallToolResult> AppRestart(
        [Description("friendly application name to restart (e.g. \"notepad\")")] string? app = null,
        [Description("process name of the app to restart")] string? process = null,
        [Description("explicitly restart the Inbrisk SERVER itself (default false)")] bool server = false,
        [Description("force hard-kill the target if it will not close gracefully — only when the user explicitly asked to restart this app (default false)")] bool force = false,
        CancellationToken ct = default)
    {
        // P1/F11: restart closes and re-spawns processes — it must honour the
        // latched emergency stop like every other mutating tool.
        if (_s.Control.ActionToken() == null)
            return Error(OutcomeKind.EmergencyStopped, StoppedDetail);

        if (server)
        {
            // deliberate fire-and-forget: respawn self after a grace delay
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(250);
                    var exe = Inbrisk.Setup.InstallLayout.CanonicalExePath;
                    if (!File.Exists(exe))
                        exe = Process.GetCurrentProcess().MainModule?.FileName ?? "inbrisk.exe";
                    Process.Start(new ProcessStartInfo(exe, "mcp") { UseShellExecute = true });
                }
                catch
                {
                    // Relaunch is best-effort — a fire-and-forget task must
                    // never fault; the deliberate exit still happens below.
                }
                Environment.Exit(0);
            });

            var res = new
            {
                accepted = true,
                restarting = true,
                server = true,
                message = "This operation intentionally terminates the current MCP session. The MCP host may need to reconnect or respawn Inbrisk."
            };
            return Text(JsonSerializer.Serialize(res));
        }

        if (string.IsNullOrWhiteSpace(app) && string.IsNullOrWhiteSpace(process))
        {
            return Error(OutcomeKind.Malformed,
                "Pass 'app' or 'process' to restart a user application. " +
                "To restart the Inbrisk server itself, explicitly pass server: true.");
        }

        var targetApp = app ?? process!;
        // Graceful close
        // Do NOT force-kill by default: force hard-terminates a process the
        // user may own. Only when the caller explicitly asked for a forced
        // restart does this pass through to CloseWindow's force path.
        await CloseWindow(process: targetApp, force: force, ct: ct);
        await Task.Delay(500, ct);
        // Relaunch
        return await Launch(app: targetApp, ct: ct);
    }

    [McpServerTool(Name = "computer_app_shutdown"), Description(
        "Gracefully shut down a target user application (by process name, window hwnd, " +
        "pid, or title substring). Closes application windows via WM_CLOSE, and force terminates " +
        "the process if requested with force:true. " +
        "To shut down the Inbrisk SERVER itself, explicitly pass server:true.")]
    public async Task<CallToolResult> AppShutdown(
        [Description("process name of the app to shut down, e.g. \"notepad\", \"VDenoise\"")] string? process = null,
        [Description("window handle of the app to shut down")] string? hwnd = null,
        [Description("process ID to shut down")] int? pid = null,
        [Description("substring of the window title")] string? titleContains = null,
        [Description("semantic target spec")] TargetSpec? target = null,
        [Description("force terminate process if graceful closure times out (default false)")] bool force = false,
        [Description("explicitly shut down the Inbrisk MCP SERVER itself (default false)")] bool server = false,
        CancellationToken ct = default)
    {
        // P1/F13: shutdown is a mutating op — it must honour the latched
        // emergency stop like every other mutating tool.
        if (_s.Control.ActionToken() == null)
            return Error(OutcomeKind.EmergencyStopped, StoppedDetail);

        if (server)
        {
            // deliberate fire-and-forget: exit self after the response flushes
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(250); }
                catch (Exception ex) { Debug.WriteLine($"app_shutdown server-exit delay failed: {ex.Message}"); }
                Environment.Exit(0);
            });

            var res = new
            {
                accepted = true,
                shuttingDown = true,
                server = true,
                message = "Inbrisk is shutting down gracefully."
            };
            return Text(JsonSerializer.Serialize(res));
        }

        process ??= target?.Process;
        hwnd ??= target?.Hwnd ?? target?.Window;
        titleContains ??= target?.Name ?? target?.NameContains;

        if (string.Equals(process, "inbrisk", StringComparison.OrdinalIgnoreCase) || string.Equals(process, "server", StringComparison.OrdinalIgnoreCase))
        {
            return Error(OutcomeKind.Malformed,
                "Shutting down the Inbrisk MCP server process requires explicit server: true.");
        }

        if (string.IsNullOrWhiteSpace(process) && string.IsNullOrWhiteSpace(hwnd) && !pid.HasValue && string.IsNullOrWhiteSpace(titleContains))
        {
            return Error(OutcomeKind.Malformed,
                "Pass 'process', 'hwnd', 'pid', or 'titleContains' to shut down a user application. " +
                "To shut down the Inbrisk server itself, explicitly pass server: true.");
        }

        // Delegate to CloseWindow if window targeting is used
        if (!string.IsNullOrWhiteSpace(hwnd) || !string.IsNullOrWhiteSpace(titleContains) || (!string.IsNullOrWhiteSpace(process) && !pid.HasValue))
        {
            return await CloseWindow(hwnd: hwnd, process: process, titleContains: titleContains, target: target, force: force, ct: ct);
        }

        if (pid.HasValue)
        {
            var pVal = pid.Value;

            // P1/F13: protect the target pid itself regardless of whether it
            // owns windows — an ancestor (terminal, IDE, MCP host) may own no
            // window at all, and killing it would end the AI host session.
            if (WindowService.IsAgentHostOrAncestorPid(pVal, out var ancestorReason))
            {
                return Error(OutcomeKind.PolicyDenied,
                    $"Refusing to shut down {ancestorReason}. Terminating it would end the AI host session.");
            }

            // A windowless descendant of the host tree (respawned server,
            // helper, renamed host binary) is equally un-killable — except
            // pids the agent itself launched and still owns.
            if (_s.Rt.Provenance.GetProvenance(pVal)?.OpenedByAgent != true &&
                WindowService.IsDescendantOfAgentHost(pVal, out var descendantReason))
            {
                return Error(OutcomeKind.PolicyDenied,
                    $"Refusing to shut down {descendantReason}. Terminating it would degrade the AI host session.");
            }

            string? procName;
            try
            {
                using var probe = Process.GetProcessById(pVal);
                procName = probe.ProcessName;
            }
            catch
            {
                return Error(OutcomeKind.TargetNotFound, $"No live process with PID {pVal}.");
            }

            // Name-list protection plus shared-host veto: killing these tears
            // down every window/app they host — WM_CLOSE per window is the
            // only permitted shutdown route for them.
            if (WindowService.IsProcessProtected(procName, out var procProtectReason) ||
                WindowService.IsSharedMultiWindowProcess(procName, out procProtectReason))
            {
                return Error(OutcomeKind.PolicyDenied,
                    $"Refusing to shut down {procProtectReason}. Close its windows individually with computer_close_window instead of terminating the shared process.");
            }

            var pidWins = _s.Rt.WindowService.ListWindows().Where(w => w.Pid == pVal).ToList();
            if (pidWins.Any(w => _s.Rt.WindowService.IsWindowProtected(w.Hwnd, out _)))
            {
                return Error(OutcomeKind.PolicyDenied, $"Refusing to shut down protected process (PID {pVal}).");
            }

            // A pid owning several top-level windows is a shared process —
            // never Kill it; each window must be closed individually.
            if (pidWins.Count > 1)
            {
                return Text(JsonSerializer.Serialize(new
                {
                    error = "SharedProcessKillRefused",
                    pid = pVal,
                    process = procName,
                    detail = $"Refusing to terminate '{procName}' (PID {pVal}): the process owns {pidWins.Count} top-level windows — killing it would close all of them at once, including windows outside the request. Close each window individually with computer_close_window instead.",
                    windows = pidWins.Select(w => $"0x{w.Hwnd:X}").ToList()
                }, J));
            }

            if (!force && !_s.Rt.Provenance.CanAgentCloseProcess(pVal, out var reason))
            {
                return Error(OutcomeKind.PolicyDenied, $"DENIED: {reason} Pass force: true if requested by user.");
            }

            try
            {
                using var proc = Process.GetProcessById(pVal);
                proc.Kill();
                return Text(JsonSerializer.Serialize(new
                {
                    success = true,
                    closed = true,
                    pid = pVal,
                    process = proc.ProcessName,
                    notification = $"Shut down {proc.ProcessName} (PID {pVal})."
                }, J));
            }
            catch (Exception ex)
            {
                return Error(OutcomeKind.Failed, $"Failed to shut down process PID {pVal}: {ex.Message}");
            }
        }

        return Error(OutcomeKind.Malformed, "Unable to resolve target process or window for shutdown.");
    }
}
