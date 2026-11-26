using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Inbrisk.Core;

namespace Inbrisk.Runtime;


/// <summary>
/// Configuration parameters for the Ghost Worker Daemon running inside Session 2 (InbriskAgent).
/// </summary>
public record GhostWorkerDaemonConfig
{
    /// <summary>
    /// Named pipe address to connect to (defaults to inbrisk_ghost_bus).
    /// </summary>
    public string PipeName { get; init; } = GhostIpcDefaults.PipeName;

    /// <summary>
    /// Host name running the named pipe server (defaults to local ".").
    /// </summary>
    public string ServerName { get; init; } = ".";

    /// <summary>
    /// Shared memory buffer map name for cross-session frame streaming.
    /// </summary>
    public string SharedBufferMapName { get; init; } = @"Global\InbriskGhostFrameBuffer";

    /// <summary>
    /// Shared synchronization event name for signaling ready frames.
    /// </summary>
    public string SharedEventName { get; init; } = @"Global\InbriskGhostFrameEvent";

    /// <summary>
    /// Target framerate for DXGI desktop capture loop (30-60 FPS).
    /// </summary>
    public int TargetFps { get; init; } = 60;

    /// <summary>
    /// Whether to automatically begin DXGI screen capture upon starting the daemon.
    /// </summary>
    public bool AutoStartCapture { get; init; } = true;

    /// <summary>
    /// Backoff delay in milliseconds before attempting to reconnect to the IPC pipe.
    /// </summary>
    public int ReconnectDelayMs { get; init; } = 1000;

    /// <summary>
    /// Default capture width in pixels.
    /// </summary>
    public int DefaultWidth { get; init; } = 1920;

    /// <summary>
    /// Default capture height in pixels.
    /// </summary>
    public int DefaultHeight { get; init; } = 1080;
}

/// <summary>
/// Live diagnostic and operational status of the Ghost Worker Daemon.
/// </summary>
public record GhostWorkerStatus(
    bool IsRunning,
    bool IsConnected,
    bool IsCapturing,
    int TargetFps,
    int CurrentFps,
    long TotalFramesCaptured,
    int ReconnectCount,
    int SessionId,
    int ProcessId,
    TimeSpan Uptime,
    string? LastError);



/// <summary>
/// High-speed automation input executor for Session 2.
/// Parses batch action payloads and drives native keyboard/mouse events.
