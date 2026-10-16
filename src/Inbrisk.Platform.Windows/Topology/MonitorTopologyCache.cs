using System;
using System.Collections.Generic;
using Inbrisk.Core;

namespace Inbrisk.Platform.Windows.Topology;

/// <summary>
/// Session-scoped cache for monitor topology (enumeration of monitors and virtual desktop bounds).
/// Invalidated on WM_DISPLAYCHANGE or DPI topology changes.
/// </summary>
public sealed class MonitorTopologyCache : IDisposable
{
    private readonly object _lock = new();
    private IReadOnlyList<MonitorInfo>? _cachedMonitors;
    private RectPx? _cachedVirtualDesktopBounds;
    private int _disposed;

    /// <summary>Guard against the dispose race: a session's warm-up runs on
    /// a background thread and may still be in flight while Dispose clears
    /// the cache — a late-completed monitor query must not insert into a
    /// disposed cache or the test-invisible leak returns.</summary>
    private bool Disposed => Volatile.Read(ref _disposed) != 0;

    public CacheTelemetry Telemetry { get; }

    public bool HasCachedMonitors
    {
        get
        {
            lock (_lock) return _cachedMonitors != null;
        }
    }

    public MonitorTopologyCache(CacheTelemetry? telemetry = null)
    {
        Telemetry = telemetry ?? new CacheTelemetry();
    }

    public IReadOnlyList<MonitorInfo> GetMonitors(Func<IReadOnlyList<MonitorInfo>> factory)
    {
        lock (_lock)
        {
            if (_cachedMonitors != null)
            {
                Telemetry.IncHit();
                PerfTrace.Count("monitor_topology:cache=hit");
                return _cachedMonitors;
            }
        }

        Telemetry.IncMiss();
        PerfTrace.Count("monitor_topology:cache=miss");
        var monitors = factory();

        lock (_lock)
        {
            // skip insert after dispose — a warm-up thread that lost the race
            // must not resurrect entries Invalidate() just cleared
            if (Disposed)
            {
                Telemetry.IncMiss();
                return monitors;
            }
            _cachedMonitors = monitors;
            Telemetry.IncInsert();
            return _cachedMonitors;
        }
    }

    public RectPx GetVirtualDesktopBounds(Func<RectPx> factory)
    {
        lock (_lock)
        {
            if (_cachedVirtualDesktopBounds.HasValue)
            {
                Telemetry.IncHit();
                PerfTrace.Count("monitor_topology:cache=hit");
                return _cachedVirtualDesktopBounds.Value;
            }
        }

        Telemetry.IncMiss();
        PerfTrace.Count("monitor_topology:cache=miss");
        var bounds = factory();

        lock (_lock)
        {
            if (Disposed) return bounds; // late warm-up insert after dispose
            _cachedVirtualDesktopBounds = bounds;
            Telemetry.IncInsert();
            return _cachedVirtualDesktopBounds.Value;
        }
    }

    public void Invalidate()
    {
        lock (_lock)
        {
            var hadData = _cachedMonitors != null || _cachedVirtualDesktopBounds.HasValue;
            _cachedMonitors = null;
            _cachedVirtualDesktopBounds = null;
            if (hadData)
                Telemetry.IncInvalidation();
        }
    }

    public void Dispose()
    {
        Volatile.Write(ref _disposed, 1);
        Invalidate();
    }
}
