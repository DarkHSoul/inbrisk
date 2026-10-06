using System.Collections.Concurrent;

namespace Inbrisk.Runtime.Adapters;

/// <summary>
/// Information about an open browser tab/target.
/// </summary>
public sealed record CdpTabInfo(string Id, string Title, string Url, string Type, string? WebSocketUrl);

/// <summary>
/// Authoritative lifecycle manager for persistent CDP target sessions.
/// Enforces single-flight connection establishment, target discovery caching,
/// and target lifecycle invalidation.
/// </summary>
public sealed class CdpSessionManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, CdpTargetSession> _sessions = new();
    private readonly ConcurrentDictionary<string, string> _activeTargetPerEndpoint = new();
    private readonly ConcurrentDictionary<string, Task<CdpTargetSession>> _acquireTasks = new();
    private readonly Func<Uri, TimeSpan, CancellationToken, Task<ICdpTransport>>? _transportFactory;
    private int _disposed;

    public CdpTelemetry Telemetry { get; }

    public int ActiveSessionCount => _sessions.Count(s => s.Value.IsValid);

    public event Action<string>? OnExecutionContextInvalidated;

    public CdpSessionManager(
        CdpTelemetry? telemetry = null,
        Func<Uri, TimeSpan, CancellationToken, Task<ICdpTransport>>? transportFactory = null)
    {
        Telemetry = telemetry ?? new CdpTelemetry();
        _transportFactory = transportFactory;
    }

    public static string MakeKey(int port, string? targetId, string? wsUrl = null)
        => MakeKey($"127.0.0.1:{port}", targetId, wsUrl);

    public static string MakeKey(string endpoint, string? targetId, string? wsUrl = null)
    {
        if (!string.IsNullOrWhiteSpace(targetId))
        {
            return $"{endpoint}|page:{targetId.Trim()}";
        }
        if (!string.IsNullOrWhiteSpace(wsUrl))
        {
            return $"{endpoint}|ws:{wsUrl.Trim()}";
        }
        return $"{endpoint}|ephemeral:{Guid.NewGuid():N}";
    }

    /// <summary>
    /// Gets an existing healthy session if available, avoiding target discovery.
    /// </summary>
    public bool TryGetSession(int port, string? tabId, out CdpTargetSession? session)
        => TryGetSession($"127.0.0.1:{port}", tabId, out session);

    public bool TryGetSession(string endpoint, string? tabId, out CdpTargetSession? session)
    {
        session = null;
        if (!string.IsNullOrWhiteSpace(tabId))
        {
            var key = MakeKey(endpoint, tabId);
            if (_sessions.TryGetValue(key, out var s) && s.IsValid && s.IsConnected)
            {
                session = s;
                return true;
            }
            return false;
        }

        if (_activeTargetPerEndpoint.TryGetValue(endpoint, out var activeTabId))
        {
            var key = MakeKey(endpoint, activeTabId);
            if (_sessions.TryGetValue(key, out var s) && s.IsValid && s.IsConnected)
            {
                session = s;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves or attaches to a persistent target session.
    /// If an existing healthy session matches the target, reuses it WITHOUT invoking target discovery.
    /// Concurrent requests for the same disconnected target share a single connection task.
    /// </summary>
    public Task<CdpTargetSession> GetOrCreateSessionAsync(
        int port,
        string? requestedTabId,
        Func<CancellationToken, Task<List<CdpTabInfo>>> discoveryFunc,
        CancellationToken ct = default)
        => GetOrCreateSessionAsync($"127.0.0.1:{port}", port, requestedTabId, discoveryFunc, ct);

    public Task<CdpTargetSession> GetOrCreateSessionAsync(
        string endpoint,
        int port,
        string? requestedTabId,
        Func<CancellationToken, Task<List<CdpTabInfo>>> discoveryFunc,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        // 1. Fast-path: Reuse existing valid session without running discovery
        if (TryGetSession(endpoint, requestedTabId, out var existingSession) && existingSession != null)
        {
            return Task.FromResult(existingSession);
        }

        var acquireKey = $"{endpoint}:{(string.IsNullOrWhiteSpace(requestedTabId) ? "*" : requestedTabId)}";

        // 2. Single-flight acquisition & reconnect: only ONE task performs discovery & connection
        return _acquireTasks.GetOrAdd(acquireKey, _ => Task.Run(async () =>
        {
            try
            {
                if (TryGetSession(endpoint, requestedTabId, out var s) && s != null)
                    return s;

                // Check if session exists in dictionary but needs reconnection
                if (!string.IsNullOrWhiteSpace(requestedTabId))
                {
                    var existingKey = MakeKey(endpoint, requestedTabId);
                    if (_sessions.TryGetValue(existingKey, out var unconn) && unconn.IsValid)
                    {
                        if (!unconn.IsConnected)
                        {
                            await unconn.EnsureConnectedAsync(ct).ConfigureAwait(false);
                        }
                        return unconn;
                    }
                }

                Telemetry.IncTargetDiscoveryCalls();
                var tabs = await discoveryFunc(ct).ConfigureAwait(false);

                CdpTabInfo? chosenTab = null;
                if (!string.IsNullOrWhiteSpace(requestedTabId))
                {
                    chosenTab = tabs.FirstOrDefault(t => t.Id.Equals(requestedTabId, StringComparison.OrdinalIgnoreCase));
                }

                if (chosenTab == null)
                {
                    chosenTab = tabs.FirstOrDefault(t => t.Type == "page" && !string.IsNullOrEmpty(t.WebSocketUrl))
                        ?? tabs.FirstOrDefault(t => !string.IsNullOrEmpty(t.WebSocketUrl));
                }

                if (chosenTab == null || string.IsNullOrEmpty(chosenTab.WebSocketUrl))
                {
                    throw new InvalidOperationException($"No attachable CDP page target found on endpoint {endpoint}.");
                }

                var sessionKey = MakeKey(endpoint, chosenTab.Id, chosenTab.WebSocketUrl);
                if (_sessions.TryGetValue(sessionKey, out var existing) && existing.IsValid)
                {
                    if (!existing.IsConnected)
                    {
                        await existing.EnsureConnectedAsync(ct).ConfigureAwait(false);
                    }
                    return existing;
                }

                var newSession = new CdpTargetSession(chosenTab.Id, chosenTab.WebSocketUrl, port, Telemetry, _transportFactory);
                newSession.OnExecutionContextInvalidated += tabId => OnExecutionContextInvalidated?.Invoke(tabId);
                newSession.OnTargetDestroyed += tabId => InvalidateSession(endpoint, tabId);

                await newSession.EnsureConnectedAsync(ct).ConfigureAwait(false);

                _sessions[sessionKey] = newSession;
                _activeTargetPerEndpoint[endpoint] = chosenTab.Id;
                return newSession;
            }
            finally
            {
                _acquireTasks.TryRemove(acquireKey, out Task<CdpTargetSession>? _);
            }
        }));
    }

    /// <summary>
    /// Explicitly registers or creates a session with a provided transport (for tests and benchmarks).
    /// </summary>
    public CdpTargetSession RegisterSession(int port, string targetId, string wsUrl, ICdpTransport transport)
        => RegisterSession($"127.0.0.1:{port}", port, targetId, wsUrl, transport);

    public CdpTargetSession RegisterSession(string endpoint, int port, string targetId, string wsUrl, ICdpTransport transport)
    {
        var key = MakeKey(endpoint, targetId, wsUrl);
        var session = new CdpTargetSession(targetId, wsUrl, port, Telemetry, _transportFactory);
        session.AttachTransport(transport);
        session.OnExecutionContextInvalidated += tid => OnExecutionContextInvalidated?.Invoke(tid);
        session.OnTargetDestroyed += tid => InvalidateSession(endpoint, tid);

        _sessions[key] = session;
        _activeTargetPerEndpoint[endpoint] = targetId;
        return session;
    }

    public void InvalidateSession(int port, string targetId)
        => InvalidateSession($"127.0.0.1:{port}", targetId);

    public void InvalidateSession(string endpoint, string targetId)
    {
        var key = MakeKey(endpoint, targetId);
        if (_sessions.TryRemove(key, out var session))
        {
            session.Invalidate();
        }

        if (_activeTargetPerEndpoint.TryGetValue(endpoint, out var active) && active == targetId)
        {
            _activeTargetPerEndpoint.TryRemove(endpoint, out _);
        }
    }

    /// <summary>
    /// Handles Target.targetCreated event: associates newly created target with the endpoint,
    /// updating current session state and avoiding cold target discovery.
    /// </summary>
    public void HandleTargetCreated(string endpoint, string targetId, string? wsUrl = null)
    {
        _activeTargetPerEndpoint[endpoint] = targetId;
    }

    public void HandleTargetCreated(int port, string targetId, string? wsUrl = null)
        => HandleTargetCreated($"127.0.0.1:{port}", targetId, wsUrl);

    public string? GetActiveTarget(int port) => GetActiveTarget($"127.0.0.1:{port}");
    public string? GetActiveTarget(string endpoint) =>
        _activeTargetPerEndpoint.TryGetValue(endpoint, out var id) ? id : null;

    /// <summary>
    /// Called when the browser process restarts, invalidating all cached targets and sessions on that port/endpoint.
    /// </summary>
    public void OnBrowserRestart(int port)
        => OnBrowserRestart($"127.0.0.1:{port}");

    public void OnBrowserRestart(string endpoint)
    {
        var prefix = $"{endpoint}|";
        var keysToRemove = _sessions.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var key in keysToRemove)
        {
            if (_sessions.TryRemove(key, out var session))
            {
                session.Invalidate();
            }
        }
        _activeTargetPerEndpoint.TryRemove(endpoint, out _);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        foreach (var session in _sessions.Values)
        {
            try { await session.DisposeAsync().ConfigureAwait(false); } catch { }
        }

        _sessions.Clear();
        _activeTargetPerEndpoint.Clear();
        _acquireTasks.Clear();
    }
}
