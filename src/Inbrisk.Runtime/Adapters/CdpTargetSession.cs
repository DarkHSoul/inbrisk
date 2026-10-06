using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Inbrisk.Runtime.Adapters;

/// <summary>
/// A pending CDP command awaiting a correlated response from the transport.
/// </summary>
public sealed record PendingCommand(
    int Id,
    string Method,
    bool IsMutation,
    TaskCompletionSource<JsonNode?> Tcs,
    int Epoch,
    DateTimeOffset DispatchedAt);

/// <summary>
/// Represents a captured network request record.
/// </summary>
public sealed class CdpRequestRecord
{
    public string RequestId { get; set; } = "";
    public string Type { get; set; } = "Other";
    public string Url { get; set; } = "";
    public int Status { get; set; }
    public double StartMs { get; set; }
    public double EndMs { get; set; }
    public double Bytes { get; set; }
    public bool FromCache { get; set; }
    public bool FromServiceWorker { get; set; }
    public string? ErrorText { get; set; }
    public string? BlockedReason { get; set; }
    public bool Canceled { get; set; }
}

/// <summary>
/// Represents a captured console or log entry.
/// </summary>
public sealed class CdpConsoleRecord
{
    public string Level { get; set; } = "info";
    public string Text { get; set; } = "";
    public string? Url { get; set; }
    public int? Line { get; set; }
    public string? Source { get; set; }
}

/// <summary>
/// Represents a captured runtime exception.
/// </summary>
public sealed class CdpExceptionRecord
{
    public string Text { get; set; } = "";
    public string? Url { get; set; }
    public int? Line { get; set; }
    public int? Column { get; set; }
}

/// <summary>
/// Outcome of page-side MutationObserver cancellation cleanup.
/// </summary>
public enum DomWaitCleanupOutcome
{
    None,
    Acknowledged,
    ContextDestroyed,
    TargetDestroyed,
    TransportUnavailable,
    TimedOut
}

/// <summary>
/// Persistent, multiplexed Chrome DevTools Protocol target session.
/// Owns a single authoritative receive loop, command correlation pipeline, event-driven
/// page navigation readiness, one-shot MutationObserver DOM waits, and bounded event buffers.
/// </summary>
public sealed class CdpTargetSession : IAsyncDisposable
{
    public DomWaitCleanupOutcome LastDomWaitCleanupOutcome { get; private set; }
    public string TargetId { get; }
    public string WebSocketUrl { get; }
    public int Port { get; }
    public CdpTelemetry Telemetry { get; }

    private readonly Func<Uri, TimeSpan, CancellationToken, Task<ICdpTransport>> _transportFactory;
    private ICdpTransport? _transport;
    private int _connectionEpoch;
    private int _disposed;
    private bool _isValid = true;

    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private Task? _receiveLoopTask;
    private CancellationTokenSource? _receiveCts;

    private int _nextCommandId;
    private readonly ConcurrentDictionary<int, PendingCommand> _pendingCommands = new();
    private readonly ConcurrentDictionary<int, byte> _fireAndForgetCommands = new();
    private readonly ConcurrentDictionary<string, bool> _enabledDomains = new();

    private long _navigationGeneration;
    private readonly ConcurrentDictionary<long, NavigationWaitState> _navWaiters = new();

    // Bounded event buffers
    private readonly BoundedCdpEventBuffer<CdpRequestRecord> _networkEvents;
    private readonly BoundedCdpEventBuffer<CdpConsoleRecord> _consoleEvents;
    private readonly BoundedCdpEventBuffer<CdpExceptionRecord> _exceptionEvents;

    // Track active requests by requestId to coalesce loadingFinished / responseReceived
    private readonly ConcurrentDictionary<string, CdpRequestRecord> _activeRequests = new();

    public int ConnectionEpoch => _connectionEpoch;
    public bool IsValid => _isValid && _disposed == 0;
    public bool IsConnected => _transport?.IsOpen == true && _disposed == 0;
    public int PendingCommandCount => _pendingCommands.Count;
    public bool HasActiveReceiveLoop => _receiveLoopTask != null && !_receiveLoopTask.IsCompleted;
    public long CurrentEventSequence => Math.Max(_networkEvents.CurrentSequence, _consoleEvents.CurrentSequence);

    public event Action<string>? OnExecutionContextInvalidated;
    public event Action<string>? OnTargetDestroyed;

    private sealed class NavigationWaitState
    {
        public long Generation { get; init; }
        public string? FrameId { get; set; }
        public string? LoaderId { get; set; }
        public TaskCompletionSource<bool> LoadEventTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> NetworkIdleTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public CdpTargetSession(
        string targetId,
        string webSocketUrl,
        int port,
        CdpTelemetry telemetry,
        Func<Uri, TimeSpan, CancellationToken, Task<ICdpTransport>>? transportFactory = null)
    {
        TargetId = targetId;
        WebSocketUrl = webSocketUrl;
        Port = port;
        Telemetry = telemetry;
        _transportFactory = transportFactory ?? ((uri, timeout, ct) => WebSocketCdpTransport.ConnectAsync(uri, timeout, ct).ContinueWith(t => (ICdpTransport)t.Result, ct));

        _networkEvents = new BoundedCdpEventBuffer<CdpRequestRecord>(500, () =>
        {
            Telemetry.IncBufferDroppedCount();
            Telemetry.IncEventsDropped();
        });

        _consoleEvents = new BoundedCdpEventBuffer<CdpConsoleRecord>(500, () =>
        {
            Telemetry.IncBufferDroppedCount();
            Telemetry.IncEventsDropped();
        });

        _exceptionEvents = new BoundedCdpEventBuffer<CdpExceptionRecord>(200, () =>
        {
            Telemetry.IncBufferDroppedCount();
            Telemetry.IncEventsDropped();
        });

        Telemetry.IncSessionCount();
    }

    /// <summary>
    /// Single-flight connection establishment. Multiple concurrent callers await the same connection task.
    /// </summary>
    public async Task EnsureConnectedAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (!_isValid)
            throw new InvalidOperationException($"CDP session for target '{TargetId}' has been invalidated.");

        if (_transport?.IsOpen == true)
        {
            Telemetry.IncConnectionReuses();
            return;
        }

        await _connectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_transport?.IsOpen == true)
            {
                Telemetry.IncConnectionReuses();
                return;
            }

            // Cleanup old transport if needed
            await CleanupTransportAsync().ConfigureAwait(false);

            var isReconnect = _connectionEpoch > 0;
            if (isReconnect)
            {
                Telemetry.IncReconnectAttempts();
                _enabledDomains.Clear();
                _domainEnableTasks.Clear();
            }

            var epoch = Interlocked.Increment(ref _connectionEpoch);
            try
            {
                var uri = new Uri(WebSocketUrl);
                _transport = await _transportFactory(uri, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                Telemetry.IncConnectionsOpened();
                if (isReconnect)
                {
                    Telemetry.IncReconnectSuccesses();
                }

                Telemetry.IncActiveReceiveLoops();
                _receiveCts = new CancellationTokenSource();
                _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(_transport, epoch, _receiveCts.Token));
            }
            catch
            {
                Telemetry.IncConnectionFailures();
                throw;
            }
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <summary>
    /// Explicitly attach an existing or mock transport (used in tests and benchmarks).
    /// </summary>
    public void AttachTransport(ICdpTransport transport)
    {
        _connectLock.Wait();
        try
        {
            var isReconnect = _connectionEpoch > 0;
            if (isReconnect)
            {
                Telemetry.IncReconnectAttempts();
                _enabledDomains.Clear();
                _domainEnableTasks.Clear();
            }

            var epoch = Interlocked.Increment(ref _connectionEpoch);
            _transport = transport;
            Telemetry.IncConnectionsOpened();
            if (isReconnect)
            {
                Telemetry.IncReconnectSuccesses();
            }

            Telemetry.IncActiveReceiveLoops();
            _receiveCts = new CancellationTokenSource();
            _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(_transport, epoch, _receiveCts.Token));
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private readonly ConcurrentDictionary<string, Task> _domainEnableTasks = new();

    /// <summary>
    /// Idempotent domain enable. Only dispatches wire enable command if domain is not yet active in this session.
    /// Thread-safe and single-flight across concurrent callers.
    /// </summary>
    public async Task EnsureDomainEnabledAsync(string domain, CancellationToken ct = default)
    {
        if (_enabledDomains.TryGetValue(domain, out var enabled) && enabled)
            return;

        var task = _domainEnableTasks.GetOrAdd(domain, async d =>
        {
            if (_enabledDomains.TryGetValue(d, out var already) && already)
                return;
            await SendCommandAsync($"{d}.enable", null, isMutation: false, ct: ct).ConfigureAwait(false);
            _enabledDomains[d] = true;
        });

        try
        {
            await task.ConfigureAwait(false);
        }
        finally
        {
            _domainEnableTasks.TryRemove(domain, out _);
        }
    }

    /// <summary>
    /// Dispatches a CDP command over the persistent transport with correlation by monotonic ID.
    /// Serializes outbound frames to prevent interleaving. Safe against replay of ambiguous mutations.
    /// </summary>
    public async Task<JsonNode?> SendCommandAsync(
        string method,
        object? parameters,
        bool isMutation = false,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (!_isValid)
            throw new InvalidOperationException($"CDP session for target '{TargetId}' has been invalidated.");

        await EnsureConnectedAsync(ct).ConfigureAwait(false);

        var id = Interlocked.Increment(ref _nextCommandId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var currentEpoch = _connectionEpoch;
        var pending = new PendingCommand(id, method, isMutation, tcs, currentEpoch, DateTimeOffset.UtcNow);

        _pendingCommands[id] = pending;
        Telemetry.IncPendingCommands();

        using var reg = ct.Register(() =>
        {
            if (_pendingCommands.TryRemove(id, out _))
            {
                Telemetry.DecPendingCommands();
                tcs.TrySetCanceled(ct);
            }
        });

        byte[] payloadBytes;
        try
        {
            var reqObj = new
            {
                id = id,
                method = method,
                @params = parameters
            };
            payloadBytes = JsonSerializer.SerializeToUtf8Bytes(reqObj);
        }
        catch
        {
            _pendingCommands.TryRemove(id, out _);
            Telemetry.DecPendingCommands();
            throw;
        }

        // Send serialization
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        bool sentSuccessfully = false;
        try
        {
            if (_transport?.IsOpen != true)
            {
                // Disconnected before send
                _pendingCommands.TryRemove(id, out _);
                Telemetry.DecPendingCommands();

                // Reconnect attempt for safe operations
                Telemetry.IncReconnectAttempts();
                await EnsureConnectedAsync(ct).ConfigureAwait(false);
                Telemetry.IncReconnectSuccesses();

                // Retry send once
                return await SendCommandAsync(method, parameters, isMutation, ct).ConfigureAwait(false);
            }

            Telemetry.IncCommandsSent();
            await _transport.SendAsync(payloadBytes, ct).ConfigureAwait(false);
            sentSuccessfully = true;
        }
        catch (Exception)
        {
            _pendingCommands.TryRemove(id, out _);
            Telemetry.DecPendingCommands();

            if (!sentSuccessfully)
            {
                // Never sent — allow reconnect if not cancelled
                if (!ct.IsCancellationRequested)
                {
                    Telemetry.IncReconnectAttempts();
                    try
                    {
                        await EnsureConnectedAsync(ct).ConfigureAwait(false);
                        Telemetry.IncReconnectSuccesses();
                        return await SendCommandAsync(method, parameters, isMutation, ct).ConfigureAwait(false);
                    }
                    catch { /* Fall through to original failure */ }
                }
            }
            throw;
        }
        finally
        {
            _sendLock.Release();
        }

        try
        {
            return await tcs.Task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            if (isMutation)
            {
                // Critical invariant: ambiguous mutation lost after dispatch is NOT replayed
                throw;
            }
            throw;
        }
    }

    /// <summary>
    /// Authoritative receive loop. Reads frames, correlates command responses by ID,
    /// routes events to bounded pipelines, and enforces session epoch checks.
    /// </summary>
    private async Task ReceiveLoopAsync(ICdpTransport transport, int epoch, CancellationToken ct)
    {
        try
        {
            while (transport.IsOpen && !ct.IsCancellationRequested && _disposed == 0)
            {
                string? message;
                try
                {
                    message = await transport.ReceiveMessageAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch { break; }

                if (message == null) break;

                // Session epoch check: late messages from older transports are safely ignored
                if (epoch != _connectionEpoch)
                {
                    Telemetry.IncLateEpochMessagesIgnored();
                    continue;
                }

                JsonNode? node;
                try
                {
                    node = JsonNode.Parse(message);
                }
                catch
                {
                    continue;
                }

                if (node == null) continue;

                // 1. Command Response Dispatch
                if (node["id"] is JsonValue idVal && idVal.TryGetValue<int>(out var id))
                {
                    if (_pendingCommands.TryRemove(id, out var pending))
                    {
                        Telemetry.DecPendingCommands();
                        Telemetry.IncResponsesReceived();

                        var errorNode = node["error"];
                        if (errorNode != null)
                        {
                            var errMsg = errorNode["message"]?.ToString() ?? "CDP command error";
                            pending.Tcs.TrySetException(new InvalidOperationException($"CDP Error ({pending.Method}): {errMsg}"));
                        }
                        else
                        {
                            pending.Tcs.TrySetResult(node);
                        }
                    }
                    else if (_fireAndForgetCommands.TryRemove(id, out _))
                    {
                        Telemetry.IncResponsesReceived();
                    }
                    else
                    {
                        Telemetry.IncUnknownResponseIds();
                    }
                    continue;
                }

                // 2. CDP Event Dispatch
                var method = node["method"]?.ToString();
                if (!string.IsNullOrEmpty(method))
                {
                    Telemetry.IncEventsReceived();
                    DispatchEvent(method, node["params"]);
                }
            }

            // On disconnect: fail any pending commands for this epoch exactly once
            if (epoch == _connectionEpoch)
            {
                FailPendingCommands(new IOException("CDP transport disconnected."));
            }
        }
        finally
        {
            Telemetry.DecActiveReceiveLoops();
        }
    }

    private void DispatchEvent(string method, JsonNode? prm)
    {
        switch (method)
        {
            // Target lifecycle
            case "Target.targetDestroyed":
            {
                var destroyedId = prm?["targetId"]?.ToString();
                if (destroyedId == TargetId || string.IsNullOrEmpty(destroyedId))
                {
                    Invalidate();
                    OnTargetDestroyed?.Invoke(TargetId);
                }
                break;
            }

            case "Target.detachedFromTarget":
            {
                var detachedId = prm?["targetId"]?.ToString();
                if (detachedId == TargetId || string.IsNullOrEmpty(detachedId))
                {
                    Invalidate();
                    OnTargetDestroyed?.Invoke(TargetId);
                }
                break;
            }

            // Execution context destruction
            case "Runtime.executionContextsCleared":
            case "Runtime.executionContextDestroyed":
            {
                OnExecutionContextInvalidated?.Invoke(TargetId);
                break;
            }

            // Navigation readiness: Page.loadEventFired
            case "Page.loadEventFired":
            {
                Telemetry.IncPageLoadEventCount();
                var currentGen = Interlocked.Read(ref _navigationGeneration);
                if (_navWaiters.TryGetValue(currentGen, out var waiter))
                {
                    waiter.LoadEventTcs.TrySetResult(true);
                }
                break;
            }

            // Navigation readiness: Page.lifecycleEvent (networkIdle)
            case "Page.lifecycleEvent":
            {
                Telemetry.IncLifecycleEventCount();
                var name = prm?["name"]?.ToString();
                var frameId = prm?["frameId"]?.ToString();
                var loaderId = prm?["loaderId"]?.ToString();

                if (name == "networkIdle")
                {
                    Telemetry.IncNetworkIdleCount();
                    var currentGen = Interlocked.Read(ref _navigationGeneration);
                    if (_navWaiters.TryGetValue(currentGen, out var waiter))
                    {
                        // Frame/loader correlation check: do not let iframe lifecycle satisfy main frame navigation
                        if ((waiter.FrameId == null || waiter.FrameId == frameId) &&
                            (waiter.LoaderId == null || waiter.LoaderId == loaderId))
                        {
                            waiter.NetworkIdleTcs.TrySetResult(true);
                        }
                    }
                }
                break;
            }

            // Network event ingestion
            case "Network.requestWillBeSent":
            {
                var reqId = prm?["requestId"]?.ToString();
                if (reqId != null)
                {
                    var req = new CdpRequestRecord
                    {
                        RequestId = reqId,
                        Url = prm?["request"]?["url"]?.ToString() ?? "",
                        StartMs = prm?["timestamp"]?.GetValue<double>() ?? 0
                    };
                    _activeRequests[reqId] = req;
                    _networkEvents.Enqueue(req);
                    Telemetry.IncNetworkEventsBuffered();
                }
                break;
            }

            case "Network.responseReceived":
            {
                var reqId = prm?["requestId"]?.ToString();
                if (reqId != null && _activeRequests.TryGetValue(reqId, out var req))
                {
                    var resp = prm?["response"];
                    req.Type = prm?["type"]?.ToString() ?? req.Type;
                    req.Status = resp?["status"]?.GetValue<int>() ?? 0;
                    req.FromCache = resp?["fromDiskCache"]?.GetValue<bool>() ?? false;
                    req.FromServiceWorker = resp?["fromServiceWorker"]?.GetValue<bool>() ?? false;
                }
                break;
            }

            case "Network.loadingFinished":
            {
                var reqId = prm?["requestId"]?.ToString();
                if (reqId != null && _activeRequests.TryGetValue(reqId, out var req))
                {
                    req.EndMs = prm?["timestamp"]?.GetValue<double>() ?? 0;
                    req.Bytes = prm?["encodedDataLength"]?.GetValue<double>() ?? 0;
                }
                break;
            }

            case "Network.loadingFailed":
            {
                var reqId = prm?["requestId"]?.ToString();
                if (reqId != null && _activeRequests.TryGetValue(reqId, out var req))
                {
                    req.ErrorText = prm?["errorText"]?.ToString();
                    req.BlockedReason = prm?["blockedReason"]?.ToString();
                    req.Canceled = prm?["canceled"]?.GetValue<bool>() ?? false;
                }
                break;
            }

            // Console and Log events
            case "Runtime.consoleAPICalled":
            {
                var argsArr = prm?["args"] as JsonArray;
                var stack = prm?["stackTrace"]?["callFrames"] as JsonArray;
                var entry = new CdpConsoleRecord
                {
                    Level = prm?["type"]?.ToString() ?? "log",
                    Text = argsArr == null ? "" : string.Join(' ', argsArr.Select(a => a?["value"]?.ToString() ?? a?["description"]?.ToString())),
                    Url = prm?["url"]?.ToString() ?? stack?.FirstOrDefault()?["url"]?.ToString(),
                    Line = prm?["lineNumber"]?.GetValue<int>() ?? stack?.FirstOrDefault()?["lineNumber"]?.GetValue<int>()
                };
                _consoleEvents.Enqueue(entry);
                Telemetry.IncConsoleEventsBuffered();
                break;
            }

            case "Log.entryAdded":
            {
                var e = prm?["entry"];
                var entry = new CdpConsoleRecord
                {
                    Level = e?["level"]?.ToString() ?? "info",
                    Source = e?["source"]?.ToString(),
                    Text = e?["text"]?.ToString() ?? "",
                    Url = e?["url"]?.ToString(),
                    Line = e?["lineNumber"]?.GetValue<int>()
                };
                _consoleEvents.Enqueue(entry);
                Telemetry.IncConsoleEventsBuffered();
                break;
            }

            case "Runtime.exceptionThrown":
            {
                var det = prm?["exceptionDetails"];
                var exRec = new CdpExceptionRecord
                {
                    Text = det?["text"]?.ToString() ?? det?["exception"]?["description"]?.ToString() ?? "exception",
                    Url = det?["url"]?.ToString(),
                    Line = det?["lineNumber"]?.GetValue<int>(),
                    Column = det?["columnNumber"]?.GetValue<int>()
                };
                _exceptionEvents.Enqueue(exRec);
                Telemetry.IncConsoleEventsBuffered();
                break;
            }
        }
    }

    /// <summary>
    /// Event-driven navigation with bounded fallback.
    /// Fast-path awaits Page.loadEventFired and correlated Page.lifecycleEvent (networkIdle).
    /// </summary>
    public async Task<JsonNode?> NavigateAndAwaitReadyAsync(
        string url,
        TimeSpan timeout,
        bool waitForNetworkIdle = true,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        await EnsureDomainEnabledAsync("Page", ct).ConfigureAwait(false);
        await EnsureDomainEnabledAsync("Runtime", ct).ConfigureAwait(false);

        try
        {
            await SendCommandAsync("Page.setLifecycleEventsEnabled", new { enabled = true }, ct: ct).ConfigureAwait(false);
        }
        catch { /* older CDP versions might not support lifecycle events */ }

        var gen = Interlocked.Increment(ref _navigationGeneration);
        var waiter = new NavigationWaitState { Generation = gen };
        _navWaiters[gen] = waiter;

        try
        {
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            var linkedToken = linkedCts.Token;

            // Dispatches navigation
            var navResult = await SendCommandAsync("Page.navigate", new { url }, isMutation: true, ct: linkedToken).ConfigureAwait(false);
            waiter.FrameId = navResult?["result"]?["frameId"]?.ToString();
            waiter.LoaderId = navResult?["result"]?["loaderId"]?.ToString();

            // Wait for loadEventFired fast-path
            var loadTask = waiter.LoadEventTcs.Task;
            var completedTask = await Task.WhenAny(loadTask, Task.Delay(timeout, linkedToken)).ConfigureAwait(false);

            if (completedTask == loadTask && await loadTask.ConfigureAwait(false))
            {
                // Page loadEvent fired!
                if (waitForNetworkIdle)
                {
                    // Bounded networkIdle check: wait up to 1000ms for networkIdle, but do NOT hang or fail if it doesn't fire
                    var idleTask = waiter.NetworkIdleTcs.Task;
                    var idleWinner = await Task.WhenAny(idleTask, Task.Delay(1000, linkedToken)).ConfigureAwait(false);
                    if (idleWinner == idleTask)
                    {
                        await idleTask.ConfigureAwait(false);
                    }
                }
                return navResult;
            }

            // Bounded compatibility fallback: probe readyState
            Telemetry.IncReadinessFallbackPollCount();
            var fallbackEval = await SendCommandAsync("Runtime.evaluate", new
            {
                expression = "document.readyState === 'complete' || document.readyState === 'interactive'",
                returnByValue = true
            }, ct: linkedToken).ConfigureAwait(false);

            var isReady = fallbackEval?["result"]?["result"]?["value"]?.GetValue<bool>() ?? false;
            if (isReady)
            {
                return navResult;
            }

            return navResult;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // Bounded timeout reached
            return null;
        }
        finally
        {
            _navWaiters.TryRemove(gen, out _);
        }
    }

    /// <summary>
    /// One-shot MutationObserver DOM wait using Runtime.evaluate + awaitPromise.
    /// Strictly passes selector as JSON argument (never string interpolated) and includes mandatory immediate pre-check.
    /// Manages explicit page-side observer registry in globalThis.__inbriskDomWaits for leak-free cancellation cleanup.
    /// </summary>
    public async Task<bool> WaitForSelectorAsync(
        string selector,
        TimeSpan timeout,
        bool waitDisappear = false,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        Telemetry.IncDomWaitCount();

        var timeoutMs = (int)Math.Max(100, timeout.TotalMilliseconds);
        var selectorJson = JsonSerializer.Serialize(selector);
        var waitId = $"inbrisk_wait_{Guid.NewGuid():N}";
        var waitIdJson = JsonSerializer.Serialize(waitId);

        // Safe script: selector passed as literal argument to an IIFE, never interpolated as code
        var observerScript = $@"(() => {{
            const sel = {selectorJson};
            const timeoutMs = {timeoutMs};
            const waitDisappear = {(waitDisappear ? "true" : "false")};
            const waitId = {waitIdJson};

            globalThis.__inbriskDomWaits = globalThis.__inbriskDomWaits || new Map();

            return new Promise((resolve) => {{
                let timer = null;
                let observer = null;
                let done = false;

                const cleanup = () => {{
                    if (done) return;
                    done = true;
                    if (timer) {{ clearTimeout(timer); timer = null; }}
                    if (observer) {{
                        try {{ observer.disconnect(); }} catch (_) {{}}
                        observer = null;
                    }}
                    try {{ globalThis.__inbriskDomWaits.delete(waitId); }} catch (_) {{}}
                }};

                const checkCondition = () => {{
                    try {{
                        const el = document.querySelector(sel);
                        const exists = el !== null;
                        return waitDisappear ? !exists : exists;
                    }} catch (e) {{
                        return false;
                    }}
                }};

                // 1. Mandatory immediate pre-check
                if (checkCondition()) {{
                    cleanup();
                    resolve({{ success: true, immediate: true }});
                    return;
                }}

                // 2. Set timeout
                if (timeoutMs > 0) {{
                    timer = setTimeout(() => {{
                        const satisfiedNow = checkCondition();
                        cleanup();
                        resolve({{ success: satisfiedNow, immediate: false, timeout: true }});
                    }}, timeoutMs);
                }}

                // 3. MutationObserver
                try {{
                    observer = new MutationObserver(() => {{
                        if (checkCondition()) {{
                            cleanup();
                            resolve({{ success: true, immediate: false }});
                        }}
                    }});

                    const root = document.documentElement || document.body || document;
                    observer.observe(root, {{
                        childList: true,
                        subtree: true,
                        attributes: true
                    }});

                    globalThis.__inbriskDomWaits.set(waitId, {{
                        observer: observer,
                        timer: timer,
                        settled: false
                    }});
                }} catch (e) {{
                    cleanup();
                    resolve({{ success: false, error: String(e) }});
                }}
            }});
        }})()";

        Telemetry.IncPageObserverInstall();
        Telemetry.IncActivePageDomWaits();

        try
        {
            var res = await SendCommandAsync("Runtime.evaluate", new
            {
                expression = observerScript,
                returnByValue = true,
                awaitPromise = true
            }, isMutation: false, ct: ct).ConfigureAwait(false);

            var val = res?["result"]?["result"]?["value"] as JsonObject;
            var success = val?["success"]?.GetValue<bool>() ?? false;
            var immediate = val?["immediate"]?.GetValue<bool>() ?? false;
            var isTimeout = val?["timeout"]?.GetValue<bool>() ?? false;

            Telemetry.DecActivePageDomWaits();
            Telemetry.IncPageObserverDisconnect();

            if (success)
            {
                if (immediate) Telemetry.IncDomWaitImmediateHit();
                else Telemetry.IncDomWaitMutationHit();
                return true;
            }
            else
            {
                if (isTimeout) Telemetry.IncDomWaitTimeout();
                return false;
            }
        }
        catch (OperationCanceledException)
        {
            Telemetry.DecActivePageDomWaits();
            Telemetry.IncDomWaitCancellation();
            Telemetry.IncPageObserverCancellationCleanup();

            if (_transport?.IsOpen != true)
            {
                LastDomWaitCleanupOutcome = DomWaitCleanupOutcome.TransportUnavailable;
                Telemetry.IncPageObserverCleanupDegraded();
            }
            else if (_disposed != 0)
            {
                LastDomWaitCleanupOutcome = DomWaitCleanupOutcome.TargetDestroyed;
                Telemetry.IncPageObserverCleanupDegraded();
            }
            else if (!_isValid)
            {
                LastDomWaitCleanupOutcome = DomWaitCleanupOutcome.ContextDestroyed;
                Telemetry.IncPageObserverCleanupDegraded();
            }
            else
            {
                try
                {
                    var cleanupExpr = $@"(() => {{
                        try {{
                            const m = globalThis.__inbriskDomWaits;
                            if (m && m.has({waitIdJson})) {{
                                const s = m.get({waitIdJson});
                                if (s && s.observer) {{ try {{ s.observer.disconnect(); }} catch(_) {{}} }}
                                if (s && s.timer) {{ clearTimeout(s.timer); }}
                                m.delete({waitIdJson});
                                return true;
                            }}
                        }} catch (_) {{}}
                        return false;
                    }})()";

                    using var cleanCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1000));
                    await SendCommandAsync("Runtime.evaluate", new
                    {
                        expression = cleanupExpr,
                        returnByValue = true
                    }, isMutation: false, ct: cleanCts.Token).ConfigureAwait(false);

                    LastDomWaitCleanupOutcome = DomWaitCleanupOutcome.Acknowledged;
                    Telemetry.IncPageObserverCleanupAcknowledged();
                }
                catch (OperationCanceledException)
                {
                    LastDomWaitCleanupOutcome = DomWaitCleanupOutcome.TimedOut;
                    Telemetry.IncPageObserverCleanupDegraded();
                }
                catch (Exception)
                {
                    LastDomWaitCleanupOutcome = DomWaitCleanupOutcome.TransportUnavailable;
                    Telemetry.IncPageObserverCleanupDegraded();
                }
            }

            throw;
        }
    }

    /// <summary>
    /// Queries the count of active page-side DOM wait observers in the current execution context.
    /// </summary>
    public async Task<int> GetActivePageDomWaitsCountAsync(CancellationToken ct = default)
    {
        if (_transport?.IsOpen != true || _disposed != 0) return 0;
        try
        {
            var res = await SendCommandAsync("Runtime.evaluate", new
            {
                expression = "globalThis.__inbriskDomWaits ? globalThis.__inbriskDomWaits.size : 0",
                returnByValue = true
            }, isMutation: false, ct: ct).ConfigureAwait(false);
            return res?["result"]?["result"]?["value"]?.GetValue<int>() ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Retrieves captured network and console events recorded since baseline sequence.
    /// </summary>
    public (IReadOnlyList<CdpRequestRecord> Network, IReadOnlyList<CdpConsoleRecord> Console, IReadOnlyList<CdpExceptionRecord> Exceptions, bool Truncated)
        GetCapturedEventsSince(long baselineSequence)
    {
        var net = _networkEvents.GetEventsSince(baselineSequence).Select(e => e.Data).ToList();
        var con = _consoleEvents.GetEventsSince(baselineSequence).Select(e => e.Data).ToList();
        var exc = _exceptionEvents.GetEventsSince(baselineSequence).Select(e => e.Data).ToList();
        var truncated = _networkEvents.Truncated || _consoleEvents.Truncated || _exceptionEvents.Truncated;
        return (net, con, exc, truncated);
    }

    public void Invalidate()
    {
        _isValid = false;
        FailPendingCommands(new InvalidOperationException($"CDP target '{TargetId}' was destroyed or closed."));

        foreach (var waiter in _navWaiters.Values)
        {
            waiter.LoadEventTcs.TrySetCanceled();
            waiter.NetworkIdleTcs.TrySetCanceled();
        }
        _navWaiters.Clear();

        _ = CleanupTransportAsync();
    }

    private void FailPendingCommands(Exception ex)
    {
        foreach (var kvp in _pendingCommands)
        {
            if (_pendingCommands.TryRemove(kvp.Key, out var pending))
            {
                Telemetry.DecPendingCommands();
                pending.Tcs.TrySetException(ex);
            }
        }
    }

    private async Task CleanupTransportAsync()
    {
        try { _receiveCts?.Cancel(); } catch { }

        if (_transport != null)
        {
            Telemetry.IncConnectionsClosed();
            try { await _transport.CloseAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            try { await _transport.DisposeAsync().ConfigureAwait(false); } catch { }
            _transport = null;
        }

        if (_receiveLoopTask != null)
        {
            try { await _receiveLoopTask.ConfigureAwait(false); } catch { }
            _receiveLoopTask = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _isValid = false;
        Telemetry.DecSessionCount();

        FailPendingCommands(new ObjectDisposedException(nameof(CdpTargetSession)));

        foreach (var waiter in _navWaiters.Values)
        {
            waiter.LoadEventTcs.TrySetCanceled();
            waiter.NetworkIdleTcs.TrySetCanceled();
        }
        _navWaiters.Clear();

        _networkEvents.Clear();
        _consoleEvents.Clear();
        _exceptionEvents.Clear();
        _activeRequests.Clear();
        _fireAndForgetCommands.Clear();

        await CleanupTransportAsync().ConfigureAwait(false);

        _connectLock.Dispose();
        _sendLock.Dispose();
    }
}
