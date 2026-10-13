using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
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
/// Incorporates process start-time verification to prevent stale hits across PID reuse,
/// and a live window-identity probe (owning thread id + owning pid + class name, re-checked
/// on every hit) to prevent stale hits across HWND reuse — Win32 exposes no window creation
/// timestamp, so identity signals are the only defence against a recycled handle.
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

                // 1. Verify the live window behind this handle is still the one
                // the entry describes (dead window, or hwnd recycled into a
                // different pid/thread/class -> evict, never serve stale)
                switch (ValidateIdentity(hwnd, entry))
                {
                    case IdentityCheck.WindowDead:
                        RemoveNode(node);
                        Telemetry.IncEviction();
                        break; // continue to re-resolve
                    case IdentityCheck.PidReused:
                        RemoveNode(node);
                        Telemetry.IncPidReuseReject();
                        break;
                    case IdentityCheck.HwndReused:
                        RemoveNode(node);
                        Telemetry.IncEviction();
                        break;
                    case IdentityCheck.Match:
                        // 2. Verify PID reuse via ProcessStartTime
                        if (IsPidReused(entry.Metadata.Pid, entry.Metadata.ProcessStartTime))
                        {
                            RemoveNode(node);
                            Telemetry.IncPidReuseReject();
                            // PID was reused or process exited; continue to re-resolve
                            break;
                        }
                        // Cache hit! Refresh the seen-at stamp, move to front of LRU
                        node.Value = entry with { SeenAtTicks = Stopwatch.GetTimestamp() };
                        _lruList.Remove(node);
                        _lruList.AddFirst(node);
                        Telemetry.IncHit();
                        PerfTrace.Count("hwnd_metadata:cache=hit");
                        return entry.Metadata;
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

            var identity = ProbeIdentity(hwnd);
            var newEntry = new HwndMetadataEntry(hwnd, resolved,
                identity.Tid, identity.Class,
                Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow);
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
                switch (ValidateIdentity(hwnd, entry))
                {
                    case IdentityCheck.WindowDead:
                    case IdentityCheck.HwndReused:
                        RemoveNode(node);
                        Telemetry.IncEviction();
                        metadata = null;
                        return false;
                    case IdentityCheck.PidReused:
                        RemoveNode(node);
                        Telemetry.IncPidReuseReject();
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

                node.Value = entry with { SeenAtTicks = Stopwatch.GetTimestamp() };
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

    private enum IdentityCheck { Match, WindowDead, PidReused, HwndReused }

    /// <summary>
    /// Hwnd-reuse gate: <see cref="NativeMethods.IsWindow"/> alone only proves SOME
    /// window owns the handle now. A recycled hwnd is detected by comparing every
    /// live identity signal — owning pid (cross-process reuse), owning thread id
    /// and class name (same-process reuse) — against what was captured at insert.
    /// </summary>
    private static IdentityCheck ValidateIdentity(long hwnd, HwndMetadataEntry entry)
    {
        if (!IsWindowValid(hwnd))
            return IdentityCheck.WindowDead;

        var live = ProbeIdentity(hwnd);
        if (live.Pid != entry.Metadata.Pid)
            return IdentityCheck.PidReused; // hwnd recycled into another process
        if (live.Tid != entry.OwnerThreadId ||
            !string.Equals(live.Class, entry.ClassName, StringComparison.Ordinal))
            return IdentityCheck.HwndReused; // recycled within the same process

        return IdentityCheck.Match;
    }

    /// <summary>Live identity probe: owning thread id, owning pid, class name.</summary>
    private static (uint Tid, int Pid, string? Class) ProbeIdentity(long hwnd)
    {
        var h = new IntPtr(hwnd);
        var tid = NativeMethods.GetWindowThreadProcessId(h, out var pid);
        string? cls = null;
        var sb = new StringBuilder(256);
        if (NativeMethods.GetClassNameW(h, sb, sb.Capacity) > 0)
            cls = sb.ToString();
        return (tid, pid, cls);
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
        catch (Exception e)
        {
            // Unexpected probe failure — do not treat as reuse, but do not be silent either.
            Debug.WriteLine($"hwnd-metadata pid-reuse probe fault: {e}");
            return false;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Clear();
    }

    /// <param name="OwnerThreadId">Thread id owning the hwnd at insert — immutable
    /// for a window's lifetime, so a change proves the handle was recycled.</param>
    /// <param name="ClassName">Window class at insert — a same-tid/same-pid recycled
    /// hwnd for a different control class is still rejected.</param>
    /// <param name="SeenAtTicks">Monotonic (clock-adjustment-proof) stamp of last
    /// validated hit — no create-time signal exists, so this bounds how long an
    /// identity could have silently changed.</param>
    private sealed record HwndMetadataEntry(
        long Hwnd,
        HwndMetadata Metadata,
        uint OwnerThreadId,
        string? ClassName,
        long SeenAtTicks,
        DateTimeOffset CachedAt
    );
}
