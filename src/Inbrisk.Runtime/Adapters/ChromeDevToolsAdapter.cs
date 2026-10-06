using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Inbrisk.Core;

namespace Inbrisk.Runtime.Adapters;

/// <summary>
/// Specialist adapter for Google Chrome and Chromium browsers via Chrome DevTools Protocol (CDP).
/// Allows AI hosts and Inbrisk to interact directly with web DOM, execute JavaScript,
/// navigate pages, inspect tabs, and retrieve element content with sub-millisecond latency
/// and zero coordinate drift over persistent multiplexed CDP sessions.
/// </summary>
public sealed class ChromeDevToolsAdapter : IApplicationAdapter, IAsyncDisposable
{
    public string AdapterId => "chrome_devtools";
    public string DisplayName => "Chrome DevTools / CDP Specialist Adapter";

    public IReadOnlyList<string> SupportedActions { get; } = new[]
    {
        "navigate", "click", "type", "evaluate", "get_content", "get_text", "get_html",
        "list_tabs", "new_tab", "close_tab", "activate_tab", "screenshot", "status",
        "capture", "snapshot"
    };

    private static readonly HashSet<string> ChromiumProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "brave", "vivaldi", "opera", "chromium"
    };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    public static CdpSessionManager DefaultSessionManager { get; } = new();
    public static CdpTelemetry Telemetry => DefaultSessionManager.Telemetry;

    public CdpSessionManager SessionManager { get; }

    private static readonly ConcurrentDictionary<string, List<SnapNode>> SnapshotCache = new();

    public ChromeDevToolsAdapter(CdpSessionManager? sessionManager = null)
    {
        SessionManager = sessionManager ?? DefaultSessionManager;
        SessionManager.OnExecutionContextInvalidated += tabId =>
        {
            SnapshotCache.TryRemove(tabId, out _);
        };
    }

    public bool IsApplicable(string? processName, long? hwnd)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        var clean = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4]
            : processName;
        return ChromiumProcesses.Contains(clean) ||
               clean.Contains("chrome", StringComparison.OrdinalIgnoreCase) ||
               clean.Contains("edge", StringComparison.OrdinalIgnoreCase) ||
               clean.Contains("brave", StringComparison.OrdinalIgnoreCase);
    }

    public bool CanHandle(string action, TargetRef? target = null, IReadOnlyDictionary<string, object?>? args = null)
    {
        var act = (action ?? "").ToLowerInvariant();
        return act is "navigate" or "click" or "type" or "fill" or "evaluate"
            or "get_content" or "get_text" or "get_html"
            or "list_tabs" or "get_tabs" or "new_tab" or "close_tab" or "activate_tab"
            or "screenshot" or "status" or "capture" or "snapshot";
    }

    public async Task<AdapterResult> ExecuteAsync(
        string action,
        TargetRef? target = null,
        IReadOnlyDictionary<string, object?>? args = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var act = (action ?? "").ToLowerInvariant();

        int port = ResolvePort(args);
        string baseUrl = $"http://127.0.0.1:{port}";

        // Verify Chrome DevTools connectivity — self-heal by spawning a
        // dedicated debug-enabled browser instance when the port is dead.
        JsonNode? versionInfo = null;
        try
        {
            versionInfo = await Http.GetFromJsonAsync<JsonNode>($"{baseUrl}/json/version", ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SessionManager.OnBrowserRestart(port);

            var autoLaunch = !(args?.TryGetValue("autoLaunch", out var al) ?? false) ||
                al is not (false or "false" or "False");
            if (autoLaunch && await TryLaunchDebugBrowserAsync(port, ct).ConfigureAwait(false))
            {
                try
                {
                    versionInfo = await Http.GetFromJsonAsync<JsonNode>($"{baseUrl}/json/version", ct).ConfigureAwait(false);
                }
                catch { /* fall through */ }
            }
            if (versionInfo == null)
                return new AdapterResult(
                    Success: false,
                    Method: "ChromeDevTools.Connect",
                    Detail: $"Chrome DevTools is not reachable on port {port} ({ex.Message}). " +
                            $"Start Chrome with remote debugging enabled using `computer_launch(app: \"chrome\", debugPort: {port})` " +
                            $"or pass `--remote-debugging-port={port}` in arguments.",
                    Error: ErrorCode.NotFound);
        }

        try
        {
            switch (act)
            {
                case "status":
                {
                    var tabs = await GetTabsAsync(baseUrl, ct).ConfigureAwait(false);
                    return new AdapterResult(
                        Success: true,
                        Method: "ChromeDevTools.Status",
                        Detail: $"Connected to {versionInfo?["Browser"] ?? "Chromium"} on port {port}. Open tabs: {tabs.Count}",
                        Data: new Dictionary<string, object?>
                        {
                            ["port"] = port,
                            ["browser"] = versionInfo?["Browser"]?.ToString(),
                            ["protocolVersion"] = versionInfo?["Protocol-Version"]?.ToString(),
                            ["openTabsCount"] = tabs.Count,
                            ["tabs"] = tabs
                        });
                }

                case "list_tabs":
                case "get_tabs":
                {
                    var tabs = await GetTabsAsync(baseUrl, ct).ConfigureAwait(false);
                    return new AdapterResult(
                        Success: true,
                        Method: "ChromeDevTools.ListTabs",
                        Detail: $"Found {tabs.Count} tabs/targets on port {port}",
                        Data: new Dictionary<string, object?>
                        {
                            ["port"] = port,
                            ["tabs"] = tabs
                        });
                }

                case "new_tab":
                {
                    var url = GetArgString(args, "url", "about:blank");
                    var res = await Http.PutAsync($"{baseUrl}/json/new?{Uri.EscapeDataString(url)}", null, ct).ConfigureAwait(false);
                    res.EnsureSuccessStatusCode();
                    var newTab = await res.Content.ReadFromJsonAsync<JsonNode>(cancellationToken: ct).ConfigureAwait(false);
                    return new AdapterResult(
                        Success: true,
                        Method: "ChromeDevTools.NewTab",
                        Detail: $"Created tab '{newTab?["title"]}' ({newTab?["url"]})",
                        Data: new Dictionary<string, object?>
                        {
                            ["tabId"] = newTab?["id"]?.ToString(),
                            ["url"] = newTab?["url"]?.ToString(),
                            ["title"] = newTab?["title"]?.ToString(),
                            ["webSocketDebuggerUrl"] = newTab?["webSocketDebuggerUrl"]?.ToString()
                        });
                }

                case "close_tab":
                {
                    var tabId = GetArgString(args, "tabId", GetArgString(args, "id", ""));
                    if (string.IsNullOrWhiteSpace(tabId))
                    {
                        var tabs = await GetTabsAsync(baseUrl, ct).ConfigureAwait(false);
                        var firstPage = tabs.FirstOrDefault(t => t.Type == "page");
                        if (firstPage == null)
                            return new AdapterResult(false, "ChromeDevTools.CloseTab", "no open page tab found to close", Error: ErrorCode.NotFound);
                        tabId = firstPage.Id;
                    }

                    SessionManager.InvalidateSession(port, tabId);
                    SnapshotCache.TryRemove(tabId, out _);

                    var res = await Http.GetAsync($"{baseUrl}/json/close/{tabId}", ct).ConfigureAwait(false);
                    return new AdapterResult(
                        Success: res.IsSuccessStatusCode,
                        Method: "ChromeDevTools.CloseTab",
                        Detail: res.IsSuccessStatusCode ? $"Closed tab {tabId}" : $"Failed to close tab {tabId}: {res.StatusCode}",
                        Data: new Dictionary<string, object?> { ["tabId"] = tabId });
                }

                case "activate_tab":
                {
                    var tabId = GetArgString(args, "tabId", GetArgString(args, "id", ""));
                    if (string.IsNullOrWhiteSpace(tabId))
                        return new AdapterResult(false, "ChromeDevTools.ActivateTab", "tabId or id argument is required", Error: ErrorCode.Unsupported);

                    var res = await Http.GetAsync($"{baseUrl}/json/activate/{tabId}", ct).ConfigureAwait(false);
                    return new AdapterResult(
                        Success: res.IsSuccessStatusCode,
                        Method: "ChromeDevTools.ActivateTab",
                        Detail: res.IsSuccessStatusCode ? $"Activated tab {tabId}" : $"Failed to activate tab {tabId}: {res.StatusCode}",
                        Data: new Dictionary<string, object?> { ["tabId"] = tabId });
                }

                case "navigate":
                {
                    var url = GetArgString(args, "url", "");
                    if (string.IsNullOrWhiteSpace(url))
                        return new AdapterResult(false, "ChromeDevTools.Navigate", "url argument is required", Error: ErrorCode.Unsupported);

                    var session = await ResolveSessionAsync(baseUrl, port, args, ct).ConfigureAwait(false);
                    SnapshotCache.TryRemove(session.TargetId, out _);

                    // Event-driven navigation with loadEventFired and lifecycle readiness
                    var navResult = await session.NavigateAndAwaitReadyAsync(url, TimeSpan.FromSeconds(8), waitForNetworkIdle: true, ct: ct).ConfigureAwait(false);

                    object? pageText = null;
                    if (args?.TryGetValue("includeContent", out var ic) != true ||
                        ic is not (false or "false" or "False"))
                    {
                        var eval = await session.SendCommandAsync("Runtime.evaluate", new
                        {
                            expression = "document.body ? document.body.innerText.slice(0,4000) : ''",
                            returnByValue = true
                        }, isMutation: false, ct: ct).ConfigureAwait(false);
                        pageText = eval?["result"]?["result"]?["value"]?.ToString();
                    }

                    var txt = pageText as string;
                    var data = new Dictionary<string, object?>
                    {
                        ["url"] = url,
                        ["tabId"] = session.TargetId,
                        ["frameId"] = navResult?["result"]?["frameId"]?.ToString()
                    };
                    if (txt != null) data["text"] = txt;

                    return new AdapterResult(
                        Success: true,
                        Method: "ChromeDevTools.Navigate",
                        Detail: $"Navigated tab '{session.TargetId}' to {url}" +
                            (txt is { Length: > 0 } ? " — page text included" : ""),
                        Data: data);
                }

                case "evaluate":
                {
                    var expr = GetArgString(args, "expression", GetArgString(args, "script", ""));
                    if (string.IsNullOrWhiteSpace(expr))
                        return new AdapterResult(false, "ChromeDevTools.Evaluate", "expression or script argument is required", Error: ErrorCode.Unsupported);

                    var session = await ResolveSessionAsync(baseUrl, port, args, ct).ConfigureAwait(false);

                    var cdpResult = await session.SendCommandAsync("Runtime.evaluate", new
                    {
                        expression = expr,
                        returnByValue = true,
                        awaitPromise = true
                    }, isMutation: false, ct: ct).ConfigureAwait(false);

                    var resultNode = cdpResult?["result"]?["result"];
                    var val = resultNode?["value"]?.ToString() ?? resultNode?["description"]?.ToString();
                    return new AdapterResult(
                        Success: true,
                        Method: "ChromeDevTools.Evaluate",
                        Detail: $"Script evaluated: {val}",
                        Data: new Dictionary<string, object?>
                        {
                            ["expression"] = expr,
                            ["result"] = val,
                            ["raw"] = resultNode?.ToJsonString()
                        });
                }

                case "click":
                {
                    var session = await ResolveSessionAsync(baseUrl, port, args, ct).ConfigureAwait(false);
                    var selector = GetArgString(args, "selector", "");
                    var uidStr = GetArgString(args, "uid", "");

                    if (!string.IsNullOrWhiteSpace(uidStr) && int.TryParse(uidStr, out var uid))
                    {
                        if (!SnapshotCache.TryGetValue(session.TargetId, out var nodes) ||
                            nodes.FirstOrDefault(n => n.Uid == uid) is not { } node)
                            return new AdapterResult(false, "ChromeDevTools.Click",
                                $"uid={uid} unknown or stale — run browser_snapshot first (uids are per-tab and reset on navigation)",
                                Error: ErrorCode.NotFound);

                        var box = await session.SendCommandAsync("DOM.getBoxModel",
                            new { backendNodeId = node.BackendNodeId }, isMutation: false, ct: ct).ConfigureAwait(false);
                        var quad = box?["result"]?["model"]?["content"] as JsonArray;
                        if (quad == null || quad.Count < 8)
                            return new AdapterResult(false, "ChromeDevTools.Click",
                                $"uid={uid} ({node.Role} '{node.Name}') has no box model — re-run browser_snapshot",
                                Error: ErrorCode.NotFound);

                        var cx = (quad[0]!.GetValue<double>() + quad[2]!.GetValue<double>() + quad[4]!.GetValue<double>() + quad[6]!.GetValue<double>()) / 4;
                        var cy = (quad[1]!.GetValue<double>() + quad[3]!.GetValue<double>() + quad[5]!.GetValue<double>() + quad[7]!.GetValue<double>()) / 4;

                        await session.SendCommandAsync("Input.dispatchMouseEvent",
                            new { type = "mousePressed", x = cx, y = cy, button = "left", clickCount = 1 }, isMutation: true, ct: ct).ConfigureAwait(false);
                        await session.SendCommandAsync("Input.dispatchMouseEvent",
                            new { type = "mouseReleased", x = cx, y = cy, button = "left", clickCount = 1 }, isMutation: true, ct: ct).ConfigureAwait(false);

                        return new AdapterResult(
                            Success: true,
                            Method: "ChromeDevTools.Click",
                            Detail: $"Clicked uid={uid} ({node.Role} '{node.Name}') at ({Math.Round(cx)},{Math.Round(cy)})",
                            Data: new Dictionary<string, object?>
                            {
                                ["uid"] = uid, ["role"] = node.Role, ["name"] = node.Name, ["tabId"] = session.TargetId
                            });
                    }

                    if (string.IsNullOrWhiteSpace(selector))
                        return new AdapterResult(false, "ChromeDevTools.Click", "selector or uid argument is required", Error: ErrorCode.Unsupported);

                    var script = $@"(() => {{
                        const sel = {JsonSerializer.Serialize(selector)};
                        let el = null;
                        try {{ el = document.querySelector(sel); }} catch (_) {{}}
                        if (!el) {{
                            const m = sel.match(/^(.*?):has-text\([""'](.*?)[""']\)$/);
                            if (m) el = [...document.querySelectorAll(m[1] || '*')]
                                .find(x => (x.innerText || '').includes(m[2]));
                        }}
                        if (!el) return {{ success: false, error: 'selector not found' }};
                        el.scrollIntoView({{ behavior: 'instant', block: 'center', inline: 'center' }});
                        el.click();
                        return {{ success: true, tag: el.tagName, text: (el.innerText || el.value || '').trim() }};
                    }})()";

                    var cdpResult = await session.SendCommandAsync("Runtime.evaluate", new
                    {
                        expression = script,
                        returnByValue = true,
                        awaitPromise = true
                    }, isMutation: true, ct: ct).ConfigureAwait(false);

                    var resVal = cdpResult?["result"]?["result"]?["value"];
                    var success = resVal?["success"]?.GetValue<bool>() ?? false;
                    var detail = success
                        ? $"Clicked element '{selector}' (tag: {resVal?["tag"]}, text: '{resVal?["text"]}')"
                        : $"Element '{selector}' not found in DOM";

                    return new AdapterResult(
                        Success: success,
                        Method: "ChromeDevTools.Click",
                        Detail: detail,
                        Data: new Dictionary<string, object?>
                        {
                            ["selector"] = selector,
                            ["tabId"] = session.TargetId
                        },
                        Error: success ? null : ErrorCode.NotFound);
                }

                case "type":
                case "fill":
                {
                    var selector = GetArgString(args, "selector", "");
                    var text = GetArgString(args, "text", GetArgString(args, "value", ""));
                    if (string.IsNullOrWhiteSpace(selector))
                        return new AdapterResult(false, "ChromeDevTools.Type", "selector argument is required", Error: ErrorCode.Unsupported);

                    var session = await ResolveSessionAsync(baseUrl, port, args, ct).ConfigureAwait(false);

                    var script = $@"(() => {{
                        const sel = {JsonSerializer.Serialize(selector)};
                        let el = null;
                        try {{ el = document.querySelector(sel); }} catch (_) {{}}
                        if (!el) {{
                            const m = sel.match(/^(.*?):has-text\([""'](.*?)[""']\)$/);
                            if (m) el = [...document.querySelectorAll(m[1] || '*')]
                                .find(x => (x.innerText || '').includes(m[2]));
                        }}
                        if (!el) return {{ success: false, error: 'selector not found' }};
                        el.scrollIntoView({{ behavior: 'instant', block: 'center', inline: 'center' }});
                        el.focus();
                        el.value = {JsonSerializer.Serialize(text)};
                        el.dispatchEvent(new Event('input', {{ bubbles: true }}));
                        el.dispatchEvent(new Event('change', {{ bubbles: true }}));
                        return {{ success: true, tag: el.tagName, value: el.value }};
                    }})()";

                    var cdpResult = await session.SendCommandAsync("Runtime.evaluate", new
                    {
                        expression = script,
                        returnByValue = true,
                        awaitPromise = true
                    }, isMutation: true, ct: ct).ConfigureAwait(false);

                    var resVal = cdpResult?["result"]?["result"]?["value"];
                    var success = resVal?["success"]?.GetValue<bool>() ?? false;
                    var detail = success
                        ? $"Set value of '{selector}' to '{text}'"
                        : $"Element '{selector}' not found in DOM";

                    return new AdapterResult(
                        Success: success,
                        Method: "ChromeDevTools.Type",
                        Detail: detail,
                        Data: new Dictionary<string, object?>
                        {
                            ["selector"] = selector,
                            ["value"] = text,
                            ["tabId"] = session.TargetId
                        },
                        Error: success ? null : ErrorCode.NotFound);
                }

                case "get_content":
                case "get_text":
                case "get_html":
                {
                    var selector = GetArgString(args, "selector", "");
                    var session = await ResolveSessionAsync(baseUrl, port, args, ct).ConfigureAwait(false);

                    string script;
                    if (string.IsNullOrWhiteSpace(selector))
                    {
                        script = "(() => ({ title: document.title, url: window.location.href, text: document.body ? document.body.innerText.substring(0, 10000) : '' }))()";
                    }
                    else if (act == "get_html")
                    {
                        script = $@"(() => {{
                            const el = document.querySelector({JsonSerializer.Serialize(selector)});
                            return el ? {{ html: el.outerHTML, found: true }} : {{ found: false }};
                        }})()";
                    }
                    else
                    {
                        script = $@"(() => {{
                            const el = document.querySelector({JsonSerializer.Serialize(selector)});
                            return el ? {{ text: (el.innerText || el.value || '').trim(), found: true }} : {{ found: false }};
                        }})()";
                    }

                    var cdpResult = await session.SendCommandAsync("Runtime.evaluate", new
                    {
                        expression = script,
                        returnByValue = true,
                        awaitPromise = true
                    }, isMutation: false, ct: ct).ConfigureAwait(false);

                    var resVal = cdpResult?["result"]?["result"]?["value"];
                    return new AdapterResult(
                        Success: true,
                        Method: "ChromeDevTools.GetContent",
                        Detail: $"Retrieved content from '{session.TargetId}'",
                        Data: new Dictionary<string, object?>
                        {
                            ["content"] = resVal?.ToJsonString(),
                            ["tabId"] = session.TargetId
                        });
                }

                case "screenshot":
                {
                    var session = await ResolveSessionAsync(baseUrl, port, args, ct).ConfigureAwait(false);

                    var cdpResult = await session.SendCommandAsync("Page.captureScreenshot", new { format = "png" }, isMutation: false, ct: ct).ConfigureAwait(false);
                    var b64 = cdpResult?["result"]?["data"]?.ToString();
                    return new AdapterResult(
                        Success: !string.IsNullOrEmpty(b64),
                        Method: "ChromeDevTools.Screenshot",
                        Detail: $"Captured screenshot of '{session.TargetId}'",
                        Data: new Dictionary<string, object?>
                        {
                            ["format"] = "png",
                            ["tabId"] = session.TargetId,
                            ["base64Length"] = b64?.Length ?? 0
                        });
                }

                case "capture":
                {
                    var session = await ResolveSessionAsync(baseUrl, port, args, ct).ConfigureAwait(false);

                    var durationMs = int.TryParse(GetArgString(args, "durationMs", GetArgString(args, "timeoutMs", "6000")), out var dm) ? dm : 6000;
                    var navUrl = GetArgString(args, "url", "");
                    var reload = !(args?.TryGetValue("reload", out var rv) ?? false) || rv is not (false or "false" or "False");
                    var ignoreCache = !(args?.TryGetValue("ignoreCache", out var ic) ?? false) || ic is not (false or "false" or "False");

                    var cap = await CapturePersistentAsync(session, navUrl, reload, ignoreCache, durationMs, ct).ConfigureAwait(false);
                    var consoleObj = cap.TryGetValue("console", out var co) && co is Dictionary<string, object?> cd
                        ? cd : new Dictionary<string, object?>();
                    var errors = consoleObj.TryGetValue("errors", out var e2) ? e2 : 0;
                    var warnings = consoleObj.TryGetValue("warnings", out var w2) ? w2 : 0;
                    var netObj = cap["network"] as Dictionary<string, object?>;
                    var totalReq = netObj?["summary"] is Dictionary<string, object?> s && s.TryGetValue("total", out var t) ? t : 0;

                    return new AdapterResult(
                        Success: true,
                        Method: "ChromeDevTools.Capture",
                        Detail: $"Captured {totalReq} requests ({errors} console errors, {warnings} warnings) on '{session.TargetId}'",
                        Data: cap);
                }

                case "snapshot":
                {
                    var session = await ResolveSessionAsync(baseUrl, port, args, ct).ConfigureAwait(false);

                    var snap = await SnapshotPersistentAsync(session, args, ct).ConfigureAwait(false);
                    return new AdapterResult(
                        Success: true,
                        Method: "ChromeDevTools.Snapshot",
                        Detail: $"Snapshot of '{session.TargetId}': {snap.Count} a11y nodes (uids usable in browser_click/browser_type via uid=N)",
                        Data: new Dictionary<string, object?>
                        {
                            ["tabId"] = session.TargetId,
                            ["nodeCount"] = snap.Count,
                            ["nodes"] = snap.Select(n => (object)new Dictionary<string, object?>
                            {
                                ["uid"] = n.Uid, ["role"] = n.Role, ["name"] = n.Name
                            }).ToList()
                        });
                }

                default:
                    return new AdapterResult(false, "ChromeDevTools.UnknownAction", $"unrecognized Chrome DevTools action '{action}'", Error: ErrorCode.Unsupported);
            }
        }
        catch (Exception ex)
        {
            return new AdapterResult(false, "ChromeDevTools.ExecutionError", ex.Message, Error: ErrorCode.Internal);
        }
    }

    private async Task<CdpTargetSession> ResolveSessionAsync(
        string baseUrl,
        int port,
        IReadOnlyDictionary<string, object?>? args,
        CancellationToken ct)
    {
        var requestedId = GetArgString(args, "tabId", GetArgString(args, "id", ""));
        return await SessionManager.GetOrCreateSessionAsync(
            port,
            string.IsNullOrWhiteSpace(requestedId) ? null : requestedId,
            async token =>
            {
                var tabs = await GetTabsAsync(baseUrl, token).ConfigureAwait(false);
                return tabs.Select(t => new CdpTabInfo(t.Id, t.Title, t.Url, t.Type, t.WebSocketUrl)).ToList();
            },
            ct).ConfigureAwait(false);
    }

    private sealed record SnapNode(int Uid, string Role, string Name, long BackendNodeId);

    private static async Task<List<SnapNode>> SnapshotPersistentAsync(
        CdpTargetSession session, IReadOnlyDictionary<string, object?>? args, CancellationToken ct)
    {
        var res = await session.SendCommandAsync("Accessibility.getFullAXTree", null, isMutation: false, ct: ct).ConfigureAwait(false);
        var nodes = res?["result"]?["nodes"] as JsonArray ?? new JsonArray();

        var interesting = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "link", "button", "textbox", "searchbox", "combobox", "checkbox", "radio",
            "menuitem", "tab", "option", "switch", "slider", "heading", "img",
            "listbox", "treeitem", "row", "cell", "navigation", "main", "form"
        };

        var snap = new List<SnapNode>();
        foreach (var n in nodes)
        {
            var role = n?["role"]?["value"]?.ToString() ?? "";
            var name = n?["name"]?["value"]?.ToString() ?? "";
            var ignored = n?["ignored"]?.GetValue<bool>() ?? false;
            var bid = n?["backendDOMNodeId"]?.GetValue<long>() ?? 0;
            if (ignored || bid == 0 || !interesting.Contains(role)) continue;
            if (name.Length == 0 && role is not ("img" or "heading")) continue;
            snap.Add(new SnapNode(snap.Count + 1, role, name.Length > 80 ? name[..80] : name, bid));
            if (snap.Count >= 400) break;
        }

        SnapshotCache[session.TargetId] = snap;
        return snap;
    }

    /// <summary>
    /// Captures network, console, and performance metrics over the persistent CDP target session.
    /// Ingests events continuously into bounded buffers and isolates events by monotonic baseline.
    /// </summary>
    private static async Task<Dictionary<string, object?>> CapturePersistentAsync(
        CdpTargetSession session,
        string navUrl,
        bool reload,
        bool ignoreCache,
        int durationMs,
        CancellationToken ct)
    {
        // 1. Ensure required domains are active on the persistent session
        await session.EnsureDomainEnabledAsync("Network", ct).ConfigureAwait(false);
        await session.EnsureDomainEnabledAsync("Runtime", ct).ConfigureAwait(false);
        await session.EnsureDomainEnabledAsync("Log", ct).ConfigureAwait(false);
        await session.EnsureDomainEnabledAsync("Page", ct).ConfigureAwait(false);

        const string vitalsInject = """
            window.__inbriskVitals = { lcp: null, cls: 0, inp: null, paint: {} };
            try { new PerformanceObserver(l => { const e = l.getEntries().at(-1); if (e) __inbriskVitals.lcp = Math.round(e.startTime); })
                  .observe({ type: 'largest-contentful-paint', buffered: true }); } catch (_) {}
            try { new PerformanceObserver(l => l.getEntries().forEach(e => { if (!e.hadRecentInput) __inbriskVitals.cls += e.value; }))
                  .observe({ type: 'layout-shift', buffered: true }); } catch (_) {}
            try { new PerformanceObserver(l => l.getEntries().forEach(e => __inbriskVitals.paint[e.name] = Math.round(e.startTime)))
                  .observe({ type: 'paint', buffered: true }); } catch (_) {}
            try { new PerformanceObserver(l => l.getEntries().forEach(e => { const d = e.duration || 0; if (e.interactionId && d > (__inbriskVitals.inp || 0)) __inbriskVitals.inp = Math.round(d); }))
                  .observe({ type: 'event', durationThreshold: 16, buffered: true }); } catch (_) {}
            """;

        await session.SendCommandAsync("Page.addScriptToEvaluateOnNewDocument", new { source = vitalsInject }, ct: ct).ConfigureAwait(false);

        // 2. Record event sequence baseline BEFORE reload/navigation
        var baselineSeq = session.CurrentEventSequence;

        if (navUrl.Length > 0)
        {
            await session.SendCommandAsync("Page.navigate", new { url = navUrl }, isMutation: true, ct: ct).ConfigureAwait(false);
        }
        else if (reload)
        {
            await session.SendCommandAsync("Page.reload", new { ignoreCache }, isMutation: true, ct: ct).ConfigureAwait(false);
        }

        await Task.Delay(Math.Clamp(durationMs, 500, 60_000), ct).ConfigureAwait(false);

        const string finalJs = """
            JSON.stringify((() => {
              const nav = performance.getEntriesByType('navigation')[0] || {};
              const res = performance.getEntriesByType('resource');
              return {
                title: document.title, href: location.href, readyState: document.readyState,
                domCount: document.querySelectorAll('*').length,
                links: document.querySelectorAll('a').length,
                scripts: document.querySelectorAll('script').length,
                timing: {
                  ttfb: Math.round(nav.responseStart || 0),
                  domContentLoaded: Math.round(nav.domContentLoadedEventEnd || 0),
                  load: Math.round(nav.loadEventEnd || 0)
                },
                vitals: window.__inbriskVitals || null,
                renderBlocking: res.filter(r => r.renderBlockingStatus === 'blocking')
                                   .map(r => r.name.split('/').pop()),
                resourceCount: res.length,
                transferKB: Math.round(res.reduce((s, r) => s + (r.transferSize || 0), 0) / 1024)
              };
            })())
            """;

        var evalRes = await session.SendCommandAsync("Runtime.evaluate", new
        {
            expression = finalJs,
            returnByValue = true,
            awaitPromise = true
        }, isMutation: false, ct: ct).ConfigureAwait(false);

        var evalJson = evalRes?["result"]?["result"]?["value"]?.ToString();
        JsonNode? page = null;
        try { if (evalJson != null) page = JsonNode.Parse(evalJson); } catch { }

        // 3. Extract captured events since baseline
        var (requestsList, consoleList, exceptionsList, truncated) = session.GetCapturedEventsSince(baselineSeq);

        var byType = requestsList.GroupBy(r => r.Type).ToDictionary(g => g.Key, g => g.Count());
        var biggest = requestsList
            .OrderByDescending(r => r.Bytes).Take(6)
            .Select(r => (object)new Dictionary<string, object?>
            {
                ["kb"] = Math.Round(r.Bytes / 1024), ["type"] = r.Type,
                ["durationMs"] = Math.Round((r.EndMs - r.StartMs) * 1000),
                ["fromCache"] = r.FromCache, ["fromServiceWorker"] = r.FromServiceWorker,
                ["url"] = r.Url.Length > 110 ? r.Url[..110] : r.Url
            }).ToList();

        var failed = requestsList
            .Where(r => r.Status >= 400 || r.Canceled || !string.IsNullOrEmpty(r.ErrorText))
            .Select(r => (object)new Dictionary<string, object?>
            {
                ["url"] = r.Url.Length > 110 ? r.Url[..110] : r.Url,
                ["status"] = r.Status,
                ["type"] = r.Type,
                ["errorText"] = r.ErrorText,
                ["canceled"] = r.Canceled
            }).ToList();

        var consoleEntries = consoleList.Select(c => new Dictionary<string, object?>
        {
            ["level"] = c.Level,
            ["source"] = c.Source,
            ["text"] = c.Text,
            ["url"] = c.Url,
            ["line"] = c.Line
        }).ToList();

        var errors = consoleEntries.Count(c => c["level"]?.ToString() == "error");
        var warnings = consoleEntries.Count(c => c["level"]?.ToString() is "warning" or "warn");

        var exceptions = exceptionsList.Select(e => (object)new Dictionary<string, object?>
        {
            ["text"] = e.Text,
            ["url"] = e.Url,
            ["line"] = e.Line,
            ["column"] = e.Column
        }).ToList();

        return new Dictionary<string, object?>
        {
            ["page"] = page is JsonObject po ? new Dictionary<string, object?>
            {
                ["title"] = po["title"]?.ToString(), ["href"] = po["href"]?.ToString(),
                ["readyState"] = po["readyState"]?.ToString(),
                ["domCount"] = po["domCount"], ["links"] = po["links"], ["scripts"] = po["scripts"]
            } : null,
            ["network"] = new Dictionary<string, object?>
            {
                ["summary"] = new Dictionary<string, object?>
                {
                    ["total"] = requestsList.Count,
                    ["transferKB"] = page?["transferKB"],
                    ["cached"] = requestsList.Count(r => r.FromCache),
                    ["serviceWorker"] = requestsList.Count(r => r.FromServiceWorker)
                },
                ["byType"] = byType,
                ["largest"] = biggest,
                ["failed"] = failed
            },
            ["console"] = new Dictionary<string, object?>
            {
                ["errors"] = errors, ["warnings"] = warnings,
                ["entries"] = consoleEntries.Take(50).ToList()
            },
            ["runtime"] = new Dictionary<string, object?> { ["exceptions"] = exceptions },
            ["timing"] = page?["timing"],
            ["vitals"] = page?["vitals"],
            ["renderBlocking"] = page?["renderBlocking"],
            ["truncated"] = truncated
        };
    }

    private static async Task<bool> TryLaunchDebugBrowserAsync(int port, CancellationToken ct)
    {
        var exe = FindChromiumExe();
        if (exe == null) return false;
        var profileDir = Path.Combine(Path.GetTempPath(), $"inbrisk_debug_profile_{port}");
        try
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"--remote-debugging-port={port} --remote-allow-origins=* " +
                            $"--no-first-run --no-default-browser-check " +
                            $"--user-data-dir=\"{profileDir}\"",
                UseShellExecute = false,
            });
            // The adapter is static and holds no runtime reference — record
            // the spawned browser on the ambient tracker so session teardown
            // can reap it.
            if (proc?.Id is int spawnedPid)
            {
                try
                {
                    SessionProcessTracker.Ambient?.Track(spawnedPid,
                        tool: "adapter:chrome-devtools");
                }
                catch { }
            }
        }
        catch { return false; }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(12);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await Http.GetFromJsonAsync<JsonNode>($"http://127.0.0.1:{port}/json/version", ct).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch { await Task.Delay(400, ct).ConfigureAwait(false); }
        }
        return false;
    }

    private static string? FindChromiumExe()
    {
        string Env(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var candidates = new[]
        {
            Path.Combine(Env(Environment.SpecialFolder.ProgramFiles), @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(Env(Environment.SpecialFolder.ProgramFilesX86), @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(Env(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(Env(Environment.SpecialFolder.ProgramFiles), @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(Env(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(Env(Environment.SpecialFolder.ProgramFiles), @"BraveSoftware\Brave-Browser\Application\brave.exe"),
            Path.Combine(Env(Environment.SpecialFolder.LocalApplicationData), @"BraveSoftware\Brave-Browser\Application\brave.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static int ResolvePort(IReadOnlyDictionary<string, object?>? args)
    {
        if (args != null)
        {
            if (args.TryGetValue("port", out var p) && int.TryParse(p?.ToString(), out var parsedPort))
                return parsedPort;
            if (args.TryGetValue("debugPort", out var dp) && int.TryParse(dp?.ToString(), out var parsedDp))
                return parsedDp;
        }
        return 9222;
    }

    private static string GetArgString(IReadOnlyDictionary<string, object?>? args, string key, string fallback)
    {
        if (args != null && args.TryGetValue(key, out var val) && val != null)
            return val.ToString() ?? fallback;
        return fallback;
    }

    private sealed record TabInfo(string Id, string Title, string Url, string Type, string? WebSocketUrl);

    private static async Task<List<TabInfo>> GetTabsAsync(string baseUrl, CancellationToken ct)
    {
        var rawTabs = await Http.GetFromJsonAsync<JsonArray>($"{baseUrl}/json/list", ct).ConfigureAwait(false);
        var list = new List<TabInfo>();
        if (rawTabs == null) return list;

        foreach (var node in rawTabs)
        {
            if (node == null) continue;
            var id = node["id"]?.ToString() ?? "";
            var title = node["title"]?.ToString() ?? "";
            var url = node["url"]?.ToString() ?? "";
            var type = node["type"]?.ToString() ?? "";
            var ws = node["webSocketDebuggerUrl"]?.ToString();
            list.Add(new TabInfo(id, title, url, type, ws));
        }
        return list;
    }

    public async ValueTask DisposeAsync()
    {
        await SessionManager.DisposeAsync().ConfigureAwait(false);
    }
}
