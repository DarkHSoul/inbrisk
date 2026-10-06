using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Inbrisk.Tests;

/// <summary>
/// Tracks processes created during tests and ensures clean termination on disposal,
/// strictly preserving any processes that were pre-existing, reused, or spawned outside
/// the explicit ownership of the test.
/// </summary>
public sealed class TestProcessTracker : IDisposable
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, TestProcessTracker> s_claimedPids = new();
    private readonly string[] _processNames;
    private readonly HashSet<int> _initialPids = new();
    private readonly HashSet<int> _explicitOwnedPids = new();
    private readonly HashSet<int> _disownedPids = new();
    private readonly Dictionary<int, string?> _ownedRuns = new();
    private readonly DateTime _startedAt = DateTime.UtcNow;
    private readonly object _lock = new();
    private readonly IntPtr _hJob = IntPtr.Zero;
    private readonly bool _allowInference;
    private bool _disposed;

    // --- Windows Job Object Win32 P/Invokes ---
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int JobObjectInformationClass, IntPtr lpJobObjectInformation, uint cbJobObjectInformationLength);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryLimit;
        public UIntPtr PeakJobMemoryLimit;
    }

    public TestProcessTracker(params string[] processNames) : this(allowInference: false, processNames) { }

    public TestProcessTracker(bool allowInference, params string[] processNames)
    {
        _allowInference = allowInference;
        _processNames = processNames;
        foreach (var name in _processNames)
        {
            var cleanName = Path.GetFileNameWithoutExtension(name);
            try
            {
                foreach (var p in Process.GetProcessesByName(cleanName))
                {
                    _initialPids.Add(p.Id);
                }
            }
            catch { }
        }

        try
        {
            _hJob = CreateJobObject(IntPtr.Zero, null);
            if (_hJob != IntPtr.Zero)
            {
                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                    {
                        LimitFlags = 0x2000 // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                    }
                };
                var length = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
                var ptr = System.Runtime.InteropServices.Marshal.AllocHGlobal((int)length);
                try
                {
                    System.Runtime.InteropServices.Marshal.StructureToPtr(info, ptr, false);
                    SetInformationJobObject(_hJob, 9 /* JobObjectExtendedLimitInformation */, ptr, length);
                }
                finally
                {
                    System.Runtime.InteropServices.Marshal.FreeHGlobal(ptr);
                }
            }
        }
        catch { }
    }

    /// <summary>PIDs that were running before tracking began (OwnedByTest = false).</summary>
    public IReadOnlySet<int> InitialPids => _initialPids;

    /// <summary>Explicitly track a PID spawned by the test as owned (OwnedByTest = true).</summary>
    public void TrackPid(int pid) => TrackOwnedProcess(pid);

    /// <summary>Explicitly track a Process instance spawned by the test.</summary>
    public void TrackProcess(Process proc) => TrackOwnedProcess(proc);

    /// <summary>Explicitly tracks a test-owned process, assigning it to the Job Object.</summary>
    public void TrackOwnedProcess(Process proc, string? runId = null)
    {
        if (proc == null) return;
        TrackOwnedProcess(proc.Id, proc.StartTime, runId);
        try
        {
            if (_hJob != IntPtr.Zero && !proc.HasExited)
            {
                AssignProcessToJobObject(_hJob, proc.Handle);
            }
        }
        catch { }
    }

    /// <summary>Explicitly tracks a PID as test-owned, ensuring it is isolated from user processes.</summary>
    public void TrackOwnedProcess(int pid, DateTime? startTime = null, string? runId = null)
    {
        lock (_lock)
        {
            if (!_initialPids.Contains(pid))
            {
                _explicitOwnedPids.Add(pid);
                _disownedPids.Remove(pid);
                _ownedRuns[pid] = runId;
                s_claimedPids[pid] = this;

                if (_hJob != IntPtr.Zero)
                {
                    var hProc = OpenProcess(0x1F0FFF /* PROCESS_ALL_ACCESS */, false, pid);
                    if (hProc != IntPtr.Zero)
                    {
                        try { AssignProcessToJobObject(_hJob, hProc); }
                        finally { CloseHandle(hProc); }
                    }
                }
            }
        }
    }

    /// <summary>Mark a PID as not owned by the test (e.g. Reused or AlreadyRunning).</summary>
    public void Disown(int pid)
    {
        lock (_lock)
        {
            _explicitOwnedPids.Remove(pid);
            _disownedPids.Add(pid);
            _ownedRuns.Remove(pid);
            s_claimedPids.TryRemove(pid, out _);
        }
    }

    /// <summary>Verifies whether a given PID is owned by this test tracker.</summary>
    public bool IsOwnedByTest(int pid)
    {
        lock (_lock)
        {
            if (_disownedPids.Contains(pid)) return false;
            if (_initialPids.Contains(pid)) return false;
            if (_explicitOwnedPids.Contains(pid)) return true;
            if (s_claimedPids.TryGetValue(pid, out var owner) && owner != this) return false;

            // Strict mode: if inference is disabled, only explicit registrations count!
            if (!_allowInference) return false;

            // For auto-discovered processes by name: check start time strictly
            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.StartTime.ToUniversalTime() >= _startedAt.AddSeconds(-2))
                    return true;
            }
            catch { }

            return false;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        var pidsToClean = new HashSet<int>();
        lock (_lock)
        {
            foreach (var id in _explicitOwnedPids)
            {
                s_claimedPids.TryRemove(id, out _);
                if (!_disownedPids.Contains(id)) pidsToClean.Add(id);
            }
        }

        if (_allowInference)
        {
            foreach (var name in _processNames)
            {
                var cleanName = Path.GetFileNameWithoutExtension(name);
                try
                {
                    foreach (var p in Process.GetProcessesByName(cleanName))
                    {
                        if (IsOwnedByTest(p.Id))
                            pidsToClean.Add(p.Id);
                    }
                }
                catch { }
            }
        }

        foreach (var pid in pidsToClean)
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                if (proc.HasExited) continue;

                // Attempt graceful close first
                try
                {
                    if (proc.CloseMainWindow())
                    {
                        if (proc.WaitForExit(300)) continue;
                    }
                }
                catch { }

                // Force kill process tree if graceful close failed or window was absent
                try
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(1000);
                }
                catch
                {
                    try { proc.Kill(); } catch { }
                }
            }
            catch (ArgumentException) { /* Process already exited */ }
            catch (InvalidOperationException) { /* Process already exited */ }
            catch { }
        }

        // Closing Job Object handle triggers JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if (_hJob != IntPtr.Zero)
        {
            try { CloseHandle(_hJob); } catch { }
        }
    }
}
