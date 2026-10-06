using System.Collections.Concurrent;
using Inbrisk.Core;

namespace Inbrisk.Platform.Windows.Indicator;

public sealed record WindowActivityToken(
    Guid Id,
    long Hwnd,
    string OwnerId,
    DateTimeOffset StartedAt,
    DateTimeOffset LastActivityAt);

/// <summary>
/// Headless-safe, thread-safe, multi-session aware Target Window Activity Highlight Service.
/// Implements transient activity token lifecycle: yellow border/highlight is shown while
/// an operation actively targets a window, and deterministically cleared on completion,
/// failure, cancellation, timeout, exception, or session disconnect.
/// </summary>
public sealed class TargetHighlightService : ITargetHighlightService
{
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<Guid, WindowActivityToken>> _tokensByHwnd = new();
    private readonly Timer? _watchdogTimer;
    private readonly int _watchdogTimeoutMs;
    private volatile bool _emergency;
    private bool _disposed;

    public int WatchdogTimeoutMs => _watchdogTimeoutMs;

    public TargetHighlightService(int watchdogTimeoutMs = 15000, bool enableWatchdog = true)
    {
        _watchdogTimeoutMs = watchdogTimeoutMs;
        if (enableWatchdog)
        {
            _watchdogTimer = new Timer(_ => RunWatchdogScan(), null, 1000, 1000);
        }
    }

    public IDisposable BeginWindowActivity(long hwnd, string? ownerId = null)
    {
        if (hwnd <= 0 || _disposed || _emergency)
        {
            return EmptyDisposable.Instance;
        }

        var token = new WindowActivityToken(
            Id: Guid.NewGuid(),
            Hwnd: hwnd,
            OwnerId: string.IsNullOrWhiteSpace(ownerId) ? "default" : ownerId,
            StartedAt: DateTimeOffset.UtcNow,
            LastActivityAt: DateTimeOffset.UtcNow);

        var bag = _tokensByHwnd.GetOrAdd(hwnd, _ => new ConcurrentDictionary<Guid, WindowActivityToken>());
        bag[token.Id] = token;

        return new TokenLease(this, hwnd, token.Id);
    }

    public bool IsWindowHighlighted(long hwnd)
    {
        if (_emergency || hwnd <= 0) return false;
        if (_tokensByHwnd.TryGetValue(hwnd, out var bag))
        {
            return !bag.IsEmpty;
        }
        return false;
    }

    public IReadOnlySet<long> GetHighlightedWindows()
    {
        if (_emergency) return new HashSet<long>();
        var set = new HashSet<long>();
        foreach (var kvp in _tokensByHwnd)
        {
            if (!kvp.Value.IsEmpty)
            {
                set.Add(kvp.Key);
            }
        }
        return set;
    }

    public int ActiveTokenCount(long hwnd)
    {
        if (_tokensByHwnd.TryGetValue(hwnd, out var bag))
        {
            return bag.Count;
        }
        return 0;
    }

    public void ClearAll()
    {
        _tokensByHwnd.Clear();
    }

    public void SetEmergency(bool stopped)
    {
        _emergency = stopped;
        if (stopped)
        {
            ClearAll();
        }
    }

    public void OnSessionDisconnected(string ownerId)
    {
        if (string.IsNullOrWhiteSpace(ownerId)) return;
        foreach (var kvp in _tokensByHwnd)
        {
            var tokens = kvp.Value.Values.Where(t => string.Equals(t.OwnerId, ownerId, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var t in tokens)
            {
                kvp.Value.TryRemove(t.Id, out _);
            }
            if (kvp.Value.IsEmpty)
            {
                _tokensByHwnd.TryRemove(kvp.Key, out _);
            }
        }
    }

    public void OnWindowDestroyed(long hwnd)
    {
        _tokensByHwnd.TryRemove(hwnd, out _);
    }

    private void EndToken(long hwnd, Guid tokenId)
    {
        if (_tokensByHwnd.TryGetValue(hwnd, out var bag))
        {
            bag.TryRemove(tokenId, out _);
            if (bag.IsEmpty)
            {
                _tokensByHwnd.TryRemove(hwnd, out _);
            }
        }
    }

    private void RunWatchdogScan()
    {
        if (_disposed || _emergency) return;
        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddMilliseconds(-_watchdogTimeoutMs);

        foreach (var kvp in _tokensByHwnd)
        {
            var stale = kvp.Value.Values.Where(t => t.LastActivityAt < cutoff).ToList();
            foreach (var t in stale)
            {
                kvp.Value.TryRemove(t.Id, out _);
            }
            if (kvp.Value.IsEmpty)
            {
                _tokensByHwnd.TryRemove(kvp.Key, out _);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _watchdogTimer?.Dispose();
            ClearAll();
        }
    }

    private sealed class TokenLease(TargetHighlightService service, long hwnd, Guid tokenId) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                service.EndToken(hwnd, tokenId);
            }
        }
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new();
        public void Dispose() { }
    }
}
