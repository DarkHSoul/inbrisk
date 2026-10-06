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
        Invalidate();
    }
}
