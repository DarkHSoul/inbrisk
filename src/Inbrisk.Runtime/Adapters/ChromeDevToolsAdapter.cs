using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Inbrisk.Core;

namespace Inbrisk.Runtime.Adapters;

/// <summary>
/// Specialist adapter for Google Chrome and Chromium browsers via Chrome DevTools Protocol (CDP).
/// Allows AI hosts and Inbrisk to interact directly with web DOM, execute JavaScript,
/// navigate pages, inspect tabs, and retrieve element content with sub-millisecond latency
/// and zero coordinate drift, bypassing heavy UIA accessibility tree crawling.
/// </summary>
public sealed class ChromeDevToolsAdapter : IApplicationAdapter
{
    public string AdapterId => "chrome_devtools";
    public string DisplayName => "Chrome DevTools / CDP Specialist Adapter";

    public IReadOnlyList<string> SupportedActions { get; } = new[]
    {
        "navigate", "click", "type", "evaluate", "get_content", "get_text", "get_html",
        "list_tabs", "new_tab", "close_tab", "activate_tab", "screenshot", "status"
    };

    private static readonly HashSet<string> ChromiumProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "brave", "vivaldi", "opera", "chromium"
    };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

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
            or "screenshot" or "status";
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
        // dedicated debug-enabled browser instance when the port is dead,
        // so `computer_adapter` alone is enough to "do things in Chrome".
        JsonNode? versionInfo = null;
        try
        {
            versionInfo = await Http.GetFromJsonAsync<JsonNode>($"{baseUrl}/json/version", ct);
        }
        catch (Exception ex)
        {
            // autoLaunch:false probes without side effects — pure status check
            var autoLaunch = !(args?.TryGetValue("autoLaunch", out var al) ?? false) ||
                al is not (false or "false" or "False");
            if (autoLaunch && await TryLaunchDebugBrowserAsync(port, ct))
            {
                try
                {
                    versionInfo = await Http.GetFromJsonAsync<JsonNode>($"{baseUrl}/json/version", ct);
                }
                catch { /* fall through to the error below */ }
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
                    var tabs = await GetTabsAsync(baseUrl, ct);
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
                    var tabs = await GetTabsAsync(baseUrl, ct);
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
                    var res = await Http.PutAsync($"{baseUrl}/json/new?{Uri.EscapeDataString(url)}", null, ct);
                    res.EnsureSuccessStatusCode();
                    var newTab = await res.Content.ReadFromJsonAsync<JsonNode>(cancellationToken: ct);
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
                        var tabs = await GetTabsAsync(baseUrl, ct);
                        var firstPage = tabs.FirstOrDefault(t => t.Type == "page");
                        if (firstPage == null)
                            return new AdapterResult(false, "ChromeDevTools.CloseTab", "no open page tab found to close", Error: ErrorCode.NotFound);
                        tabId = firstPage.Id;
                    }

                    var res = await Http.GetAsync($"{baseUrl}/json/close/{tabId}", ct);
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

                    var res = await Http.GetAsync($"{baseUrl}/json/activate/{tabId}", ct);
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

                    var targetTab = await ResolveActivePageTabAsync(baseUrl, args, ct);
                    if (targetTab?.WebSocketUrl == null)
                        return new AdapterResult(false, "ChromeDevTools.Navigate", "no attachable page target found", Error: ErrorCode.NotFound);

                    var cdpResult = await SendCdpCommandAsync(targetTab.WebSocketUrl, "Page.navigate", new { url = url }, ct);
                    return new AdapterResult(
                        Success: true,
                        Method: "ChromeDevTools.Navigate",
                        Detail: $"Navigated tab '{targetTab.Title}' to {url}",
                        Data: new Dictionary<string, object?>
                        {
                            ["url"] = url,
                            ["tabId"] = targetTab.Id,
                            ["frameId"] = cdpResult?["result"]?["frameId"]?.ToString()
                        });
                }

                case "evaluate":
                {
                    var expr = GetArgString(args, "expression", GetArgString(args, "script", ""));
                    if (string.IsNullOrWhiteSpace(expr))
                        return new AdapterResult(false, "ChromeDevTools.Evaluate", "expression or script argument is required", Error: ErrorCode.Unsupported);

                    var targetTab = await ResolveActivePageTabAsync(baseUrl, args, ct);
                    if (targetTab?.WebSocketUrl == null)
                        return new AdapterResult(false, "ChromeDevTools.Evaluate", "no attachable page target found", Error: ErrorCode.NotFound);

                    var cdpResult = await SendCdpCommandAsync(targetTab.WebSocketUrl, "Runtime.evaluate", new
                    {
                        expression = expr,
                        returnByValue = true,
                        awaitPromise = true
                    }, ct);

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
                    var selector = GetArgString(args, "selector", "");
                    if (string.IsNullOrWhiteSpace(selector))
                        return new AdapterResult(false, "ChromeDevTools.Click", "selector argument is required", Error: ErrorCode.Unsupported);

                    var targetTab = await ResolveActivePageTabAsync(baseUrl, args, ct);
                    if (targetTab?.WebSocketUrl == null)
                        return new AdapterResult(false, "ChromeDevTools.Click", "no attachable page target found", Error: ErrorCode.NotFound);

                    var script = $@"(() => {{
                        const el = document.querySelector({JsonSerializer.Serialize(selector)});
                        if (!el) return {{ success: false, error: 'selector not found' }};
                        el.scrollIntoView({{ behavior: 'instant', block: 'center', inline: 'center' }});
                        el.click();
                        return {{ success: true, tag: el.tagName, text: (el.innerText || el.value || '').trim() }};
                    }})()";

                    var cdpResult = await SendCdpCommandAsync(targetTab.WebSocketUrl, "Runtime.evaluate", new
                    {
                        expression = script,
                        returnByValue = true,
                        awaitPromise = true
                    }, ct);

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
                            ["tabId"] = targetTab.Id
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

                    var targetTab = await ResolveActivePageTabAsync(baseUrl, args, ct);
                    if (targetTab?.WebSocketUrl == null)
                        return new AdapterResult(false, "ChromeDevTools.Type", "no attachable page target found", Error: ErrorCode.NotFound);

                    var script = $@"(() => {{
                        const el = document.querySelector({JsonSerializer.Serialize(selector)});
                        if (!el) return {{ success: false, error: 'selector not found' }};
                        el.scrollIntoView({{ behavior: 'instant', block: 'center', inline: 'center' }});
                        el.focus();
                        el.value = {JsonSerializer.Serialize(text)};
                        el.dispatchEvent(new Event('input', {{ bubbles: true }}));
                        el.dispatchEvent(new Event('change', {{ bubbles: true }}));
                        return {{ success: true, tag: el.tagName, value: el.value }};
                    }})()";

                    var cdpResult = await SendCdpCommandAsync(targetTab.WebSocketUrl, "Runtime.evaluate", new
                    {
                        expression = script,
                        returnByValue = true,
                        awaitPromise = true
                    }, ct);

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
                            ["tabId"] = targetTab.Id
                        },
                        Error: success ? null : ErrorCode.NotFound);
                }

                case "get_content":
                case "get_text":
                case "get_html":
                {
                    var selector = GetArgString(args, "selector", "");
                    var targetTab = await ResolveActivePageTabAsync(baseUrl, args, ct);
                    if (targetTab?.WebSocketUrl == null)
                        return new AdapterResult(false, "ChromeDevTools.GetContent", "no attachable page target found", Error: ErrorCode.NotFound);

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

                    var cdpResult = await SendCdpCommandAsync(targetTab.WebSocketUrl, "Runtime.evaluate", new
                    {
                        expression = script,
                        returnByValue = true,
                        awaitPromise = true
                    }, ct);

                    var resVal = cdpResult?["result"]?["result"]?["value"];
                    return new AdapterResult(
                        Success: true,
                        Method: "ChromeDevTools.GetContent",
                        Detail: $"Retrieved content from '{targetTab.Title}'",
                        Data: new Dictionary<string, object?>
                        {
                            ["content"] = resVal?.ToJsonString(),
                            ["tabId"] = targetTab.Id
                        });
                }

                case "screenshot":
                {
                    var targetTab = await ResolveActivePageTabAsync(baseUrl, args, ct);
                    if (targetTab?.WebSocketUrl == null)
                        return new AdapterResult(false, "ChromeDevTools.Screenshot", "no attachable page target found", Error: ErrorCode.NotFound);

                    var cdpResult = await SendCdpCommandAsync(targetTab.WebSocketUrl, "Page.captureScreenshot", new { format = "png" }, ct);
                    var b64 = cdpResult?["result"]?["data"]?.ToString();
                    return new AdapterResult(
                        Success: !string.IsNullOrEmpty(b64),
                        Method: "ChromeDevTools.Screenshot",
                        Detail: $"Captured screenshot of '{targetTab.Title}'",
                        Data: new Dictionary<string, object?>
                        {
                            ["format"] = "png",
                            ["tabId"] = targetTab.Id,
                            ["base64Length"] = b64?.Length ?? 0
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

    /// <summary>
    /// Spawns a debug-enabled Chromium browser (own temp profile, same
    /// convention as AppService's debugPort launch) and waits for CDP.
    /// A separate user-data-dir is mandatory: handing flags to a running
    /// Chrome silently ignores --remote-debugging-port.
    /// </summary>
    private static async Task<bool> TryLaunchDebugBrowserAsync(int port, CancellationToken ct)
    {
        var exe = FindChromiumExe();
        if (exe == null) return false;
        var profileDir = Path.Combine(Path.GetTempPath(), $"inbrisk_debug_profile_{port}");
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"--remote-debugging-port={port} --remote-allow-origins=* " +
                            $"--no-first-run --no-default-browser-check " +
                            $"--user-data-dir=\"{profileDir}\"",
                UseShellExecute = false,
            });
        }
        catch { return false; }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(12);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await Http.GetFromJsonAsync<JsonNode>($"http://127.0.0.1:{port}/json/version", ct);
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch { await Task.Delay(400, ct); }
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
        var rawTabs = await Http.GetFromJsonAsync<JsonArray>($"{baseUrl}/json/list", ct);
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

    private static async Task<TabInfo?> ResolveActivePageTabAsync(
        string baseUrl,
        IReadOnlyDictionary<string, object?>? args,
        CancellationToken ct)
    {
        var tabs = await GetTabsAsync(baseUrl, ct);
        var requestedId = GetArgString(args, "tabId", GetArgString(args, "id", ""));
        if (!string.IsNullOrWhiteSpace(requestedId))
        {
            return tabs.FirstOrDefault(t => t.Id.Equals(requestedId, StringComparison.OrdinalIgnoreCase));
        }

        // Return first page type target with an active WebSocket debugger URL
        return tabs.FirstOrDefault(t => t.Type == "page" && !string.IsNullOrEmpty(t.WebSocketUrl))
            ?? tabs.FirstOrDefault(t => !string.IsNullOrEmpty(t.WebSocketUrl));
    }

    private static async Task<JsonNode?> SendCdpCommandAsync(
        string wsUrl,
        string method,
        object? parameters,
        CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        var uri = new Uri(wsUrl);
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(TimeSpan.FromSeconds(5));
        await ws.ConnectAsync(uri, connectCts.Token);

        var id = Random.Shared.Next(1000, 99999);
        var req = new
        {
            id = id,
            method = method,
            @params = parameters
        };

        var json = JsonSerializer.Serialize(req);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);

        // Receive response
        var buffer = new byte[64 * 1024];
        var ms = new MemoryStream();
        while (ws.State == WebSocketState.Open)
        {
            var res = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            ms.Write(buffer, 0, res.Count);
            if (res.EndOfMessage) break;
        }

        var resJson = Encoding.UTF8.GetString(ms.ToArray());
        return JsonNode.Parse(resJson);
    }
}
