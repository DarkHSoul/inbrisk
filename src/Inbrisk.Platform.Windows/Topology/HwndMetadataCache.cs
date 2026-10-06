using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Inbrisk.Core;
using Inbrisk.Platform.Windows.Native;

namespace Inbrisk.Platform.Windows.Topology;

public sealed record HwndMetadata(
    long Hwnd,
    int Pid,
    string? ProcessName,
    DateTimeOffset? ProcessStartTime,
    bool IsElevated
);

/// <summary>
/// Session-scoped cache for HWND -> process metadata (PID, ProcessName, ProcessStartTime, IsElevated).
/// Incorporates process start-time verification to prevent stale hits across PID reuse.
/// Bounds capacity to 512 entries with LRU eviction.
/// </summary>
public sealed class HwndMetadataCache : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<long, LinkedListNode<HwndMetadataEntry>> _map = new();
    private readonly LinkedList<HwndMetadataEntry> _lruList = new();
    private readonly int _maxCapacity;
    private volatile bool _disposed;

    public CacheTelemetry Telemetry { get; }

    public int EntryCount
    {
        get
        {
            lock (_lock) return _map.Count;
        }
    }

    public HwndMetadataCache(int maxCapacity = 512, CacheTelemetry? telemetry = null)
    {
        _maxCapacity = maxCapacity > 0 ? maxCapacity : 512;
        Telemetry = telemetry ?? new CacheTelemetry();
    }

    public HwndMetadata? GetOrAdd(long hwnd, Func<long, HwndMetadata?> resolver)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(hwnd, out var node))
            {
                var entry = node.Value;

                // 1. Verify HWND is still a valid window
                if (!IsWindowValid(hwnd))
                {
                    RemoveNode(node);
                    Telemetry.IncEviction();
                    // Window is dead; continue to re-resolve (which will return null or updated)
                }
                else
                {
                    // 2. Verify PID reuse via ProcessStartTime
                    if (IsPidReused(entry.Metadata.Pid, entry.Metadata.ProcessStartTime))
                    {
                        RemoveNode(node);
                        Telemetry.IncPidReuseReject();
                        // PID was reused or process exited; continue to re-resolve
                    }
                    else
                    {
                        // Cache hit! Move to front of LRU
                        _lruList.Remove(node);
                        _lruList.AddFirst(node);
                        Telemetry.IncHit();
                        PerfTrace.Count("hwnd_metadata:cache=hit");
                        return entry.Metadata;
                    }
                }
            }
        }

        Telemetry.IncMiss();
        PerfTrace.Count("hwnd_metadata:cache=miss");
        var resolved = resolver(hwnd);
        if (resolved == null)
            return null;

        lock (_lock)
        {
            if (_disposed)
                return resolved;

            if (_map.TryGetValue(hwnd, out var existingNode))
            {
                RemoveNode(existingNode);
            }

            while (_map.Count >= _maxCapacity && _lruList.Last != null)
            {
                var oldest = _lruList.Last;
                RemoveNode(oldest);
                Telemetry.IncEviction();
            }

            var newEntry = new HwndMetadataEntry(hwnd, resolved, DateTimeOffset.UtcNow);
            var newNode = _lruList.AddFirst(newEntry);
            _map[hwnd] = newNode;
            Telemetry.IncInsert();
            return resolved;
        }
    }

    public bool TryGet(long hwnd, out HwndMetadata? metadata)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(hwnd, out var node))
            {
                var entry = node.Value;
                if (!IsWindowValid(hwnd))
                {
                    RemoveNode(node);
                    Telemetry.IncEviction();
                    metadata = null;
                    return false;
                }

                if (IsPidReused(entry.Metadata.Pid, entry.Metadata.ProcessStartTime))
                {
                    RemoveNode(node);
                    Telemetry.IncPidReuseReject();
                    metadata = null;
                    return false;
                }

                _lruList.Remove(node);
                _lruList.AddFirst(node);
                Telemetry.IncHit();
                PerfTrace.Count("hwnd_metadata:cache=hit");
                metadata = entry.Metadata;
                return true;
            }
        }

        Telemetry.IncMiss();
        PerfTrace.Count("hwnd_metadata:cache=miss");
        metadata = null;
        return false;
    }

    public bool ContainsKey(long hwnd)
    {
        lock (_lock) return _map.ContainsKey(hwnd);
    }

    public void InvalidateHwnd(long hwnd)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(hwnd, out var node))
            {
                RemoveNode(node);
                Telemetry.IncInvalidation();
            }
        }
    }

    public void InvalidatePid(int pid)
    {
        lock (_lock)
        {
            var nodesToRemove = _map.Values
                .Where(n => n.Value.Metadata.Pid == pid)
                .ToList();

            foreach (var node in nodesToRemove)
            {
                RemoveNode(node);
                Telemetry.IncInvalidation();
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            var count = _map.Count;
            _map.Clear();
            _lruList.Clear();
            if (count > 0)
                Telemetry.IncInvalidation();
        }
    }

    public bool VerifyPidGeneration(int pid, DateTimeOffset? cachedStartTime)
    {
        return !IsPidReused(pid, cachedStartTime);
    }

    private void RemoveNode(LinkedListNode<HwndMetadataEntry> node)
    {
        _map.Remove(node.Value.Hwnd);
        _lruList.Remove(node);
    }

    private static bool IsWindowValid(long hwnd)
    {
        return NativeMethods.IsWindow(new IntPtr(hwnd));
    }

    private static bool IsPidReused(int pid, DateTimeOffset? cachedStartTime)
    {
        if (pid <= 0) return false;
        try
        {
            using var proc = Process.GetProcessById(pid);
            if (proc.HasExited) return true;

            if (cachedStartTime.HasValue)
            {
                try
                {
                    var actualStartTime = proc.StartTime;
                    // Exact process-generation token: compare UTC ticks with ZERO tolerance window.
                    // A recycled PID with different creation time (< 1s difference) is immediately rejected.
                    if (actualStartTime.ToUniversalTime().Ticks != cachedStartTime.Value.UtcDateTime.Ticks)
                        return true;
                }
                catch
                {
                    // AccessDenied or process already exiting: if we cannot query start time, do not blindly assume reuse unless exited
                }
            }
            return false;
        }
        catch (ArgumentException)
        {
            // Process no longer exists
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Clear();
    }

    private sealed record HwndMetadataEntry(
        long Hwnd,
        HwndMetadata Metadata,
        DateTimeOffset CachedAt
    );
}
