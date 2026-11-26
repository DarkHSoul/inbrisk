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

/// Connects to Session 1 via Named Pipe (<c>\\.\pipe\inbrisk_ghost_bus</c>) with auto-reconnect,
/// listens for actions (execute_batch, start_capture, ping, terminate),
/// and runs an independent background DXGI desktop capture loop (30-60 FPS) feeding
/// fresh frames into the cross-session shared memory buffer (<c>GhostSharedFrameBuffer</c>).
/// </summary>
public sealed class GhostWorkerDaemon : IAsyncDisposable, IDisposable
{
    private readonly GhostWorkerDaemonConfig _config;
    private readonly GhostInputExecutor _inputExecutor;
    private readonly GhostIpcClient _ipcClient;
    private readonly object _syncRoot = new();

    private CancellationTokenSource? _cts;
    private Task? _connectLoopTask;

    // Capture Loop State
    private Thread? _captureThread;
    private volatile bool _isCapturing;
    private IGhostCaptureEngine? _captureEngine;
    private NativeGhostFrameBufferWriter? _frameWriter;

    // Diagnostic & Telemetry Counters
    private readonly Stopwatch _uptimeWatch = Stopwatch.StartNew();
    private readonly Stopwatch _fpsWatch = Stopwatch.StartNew();
    private long _totalFramesCaptured;
    private int _fpsCounter;
    private double _currentFps;
    private int _reconnectCount;
    private string? _lastError;
    private bool _isTerminated;
    private int _disposed;


    public GhostWorkerDaemonConfig Config => _config;

    public GhostInputExecutor InputExecutor => _inputExecutor;

    public bool IsRunning => _connectLoopTask != null && !_isTerminated && _disposed == 0;

    public bool IsConnected => _ipcClient.IsConnected;

    public bool IsCapturing => _isCapturing;

    public int CurrentFps => (int)Math.Round(Volatile.Read(ref _currentFps));

    public long TotalFramesCaptured => Volatile.Read(ref _totalFramesCaptured);

    public int ReconnectCount => Volatile.Read(ref _reconnectCount);

    public string? LastError => _lastError;



    public GhostWorkerDaemon(GhostWorkerDaemonConfig? config = null, GhostInputExecutor? inputExecutor = null)
    {
        _config = config ?? new GhostWorkerDaemonConfig();
        _inputExecutor = inputExecutor ?? new GhostInputExecutor();
        _ipcClient = new GhostIpcClient(_config.PipeName, _config.ServerName);

        RegisterIpcHandlers();
    }



    /// <summary>
    /// Starts the Ghost Worker Daemon background connection and capture tasks.
    /// </summary>
    public Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        lock (_syncRoot)
        {
            if (_connectLoopTask != null) return Task.CompletedTask;

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _connectLoopTask = RunConnectLoopAsync(_cts.Token);

            if (_config.AutoStartCapture)
            {
                StartCaptureInternal(_config.TargetFps);
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Runs the daemon continuously until the provided token is cancelled or a terminate action is received.
    /// </summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        await StartAsync(ct).ConfigureAwait(false);
        if (_connectLoopTask != null)
        {
            try
            {
                await _connectLoopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        }
    }

    /// <summary>
    /// Gracefully stops the worker daemon, capture loop, and IPC connection.
    /// </summary>
    public async Task StopAsync()
    {
        lock (_syncRoot)
        {
            _isTerminated = true;
            _cts?.Cancel();
            StopCaptureInternal();
        }

        if (_connectLoopTask != null)
        {
            try { await _connectLoopTask.ConfigureAwait(false); } catch { }
            _connectLoopTask = null;
        }

        await _ipcClient.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Starts the background DXGI capture loop feeding frames to the shared memory buffer.
    /// </summary>
    public bool StartCapture(int? fps = null)
    {
        lock (_syncRoot)
        {
            return StartCaptureInternal(fps ?? _config.TargetFps);
        }
    }

    /// <summary>
    /// Stops the background DXGI capture loop.
    /// </summary>
    public void StopCapture()
    {
        lock (_syncRoot)
        {
            StopCaptureInternal();
        }
    }

    /// <summary>
    /// Returns current runtime snapshot for diagnostics and RPC status calls.
    /// </summary>
    public GhostWorkerStatus GetStatus()
    {
        int sessionId = 2;
        try { sessionId = Process.GetCurrentProcess().SessionId; } catch { }

        return new GhostWorkerStatus(
            IsRunning: IsRunning,
            IsConnected: IsConnected,
            IsCapturing: IsCapturing,
            TargetFps: _config.TargetFps,
            CurrentFps: CurrentFps,
            TotalFramesCaptured: TotalFramesCaptured,
            ReconnectCount: ReconnectCount,
            SessionId: sessionId,
            ProcessId: Environment.ProcessId,
            Uptime: _uptimeWatch.Elapsed,
            LastError: _lastError);
    }



    private void RegisterIpcHandlers()
    {
        // 1. "ping" -> "pong"
        _ipcClient.RegisterHandler("ping", _ => Task.FromResult("pong"));

        // 2. "terminate" -> Clean graceful daemon shutdown
        _ipcClient.RegisterHandler("terminate", _ =>
        {
            Task.Run(async () =>
            {
                await Task.Delay(100).ConfigureAwait(false);
                await StopAsync().ConfigureAwait(false);
            });
            return Task.FromResult(JsonSerializer.Serialize(new { status = "terminating", message = "Ghost worker daemon shutting down cleanly." }));
        });

        // 3. "start_capture" -> Starts DXGI capture loop
        _ipcClient.RegisterHandler("start_capture", payloadJson =>
        {
            int fps = _config.TargetFps;

            if (!string.IsNullOrWhiteSpace(payloadJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(payloadJson);
                    if (doc.RootElement.TryGetProperty("fps", out var fpsProp) && fpsProp.TryGetInt32(out int parsedFps))
                    {
                        fps = parsedFps;
                    }
                }
                catch { }
            }

            bool ok = StartCapture(fps);
            var response = JsonSerializer.Serialize(new
            {
                status = ok ? "started" : "failed",
                isCapturing = IsCapturing,
                fps = CurrentFps,
                targetFps = _config.TargetFps,
                error = _lastError
            });
            return Task.FromResult(response);
        });

        // 4. "stop_capture" -> Stops DXGI capture loop
        _ipcClient.RegisterHandler("stop_capture", _ =>
        {
            StopCapture();
            return Task.FromResult(JsonSerializer.Serialize(new { status = "stopped", isCapturing = IsCapturing }));
        });

        // 5. "execute_batch" -> GhostInputExecutor executes batch steps
        _ipcClient.RegisterHandler("execute_batch", async payloadJson =>
        {
            if (EmergencyGate.IsStopped)
            {
                return JsonSerializer.Serialize(new { status = "emergency_stopped", error = "Emergency stop is engaged" });
            }
            var result = await _inputExecutor.ExecuteBatchAsync(payloadJson, _cts?.Token ?? default).ConfigureAwait(false);
            return JsonSerializer.Serialize(result);
        });

        // 5b. "forward_input" -> Real-time mouse and input forwarded from interactive PiP
        _ipcClient.RegisterHandler("forward_input", payloadJson =>
        {
            if (!string.IsNullOrWhiteSpace(payloadJson))
            {
                try
                {
                    var packet = GhostInputPacket.FromJson(payloadJson);
                    if (packet != null)
                    {
                        ExecuteForwardedInput(packet);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[GhostWorkerDaemon] forward_input execution error: {ex.Message}");
                }
            }
            return Task.FromResult(JsonSerializer.Serialize(new { status = "ok" }));
        });

        // 5c. "forward_keyboard" -> Real-time keyboard input forwarded from interactive PiP
        _ipcClient.RegisterHandler("forward_keyboard", payloadJson =>
        {
            if (!string.IsNullOrWhiteSpace(payloadJson))
            {
                try
                {
                    ExecuteForwardedKeyboard(payloadJson);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[GhostWorkerDaemon] forward_keyboard execution error: {ex.Message}");
                }
            }
            return Task.FromResult(JsonSerializer.Serialize(new { status = "ok" }));
        });

        // 6. "status" -> Live diagnostic summary
        _ipcClient.RegisterHandler("status", _ => Task.FromResult(JsonSerializer.Serialize(GetStatus())));
    }

    private void ExecuteForwardedInput(GhostInputPacket packet)
    {
        var type = packet.Type?.ToLowerInvariant() ?? "";
        var action = packet.Action?.ToLowerInvariant() ?? "";
        var btn = PipInputTranslator.ParseMouseButton(packet.Button);

        // Emergency gate: mutating input is refused while stopped; release
        // passes so panic always unwinds held ghost-side state.
        if (EmergencyGate.IsStopped && !type.Contains("release") && action != "release_all")
        {
            return;
        }

        if (type.Contains("move") || action == "move")
        {
            _inputExecutor.InputService.MoveMouse(packet.X, packet.Y);
        }
        else if (type.Contains("down") || action == "down")
        {
            _inputExecutor.InputService.MoveMouse(packet.X, packet.Y);
            if (_inputExecutor.InputService is GhostDesktopInput.ILocalMouseControl mc)
            {
                mc.MouseDown(btn, packet.X, packet.Y);
            }
            else
            {
                _inputExecutor.InputService.Click(packet.X, packet.Y, btn, 1);
            }
        }
        else if (type.Contains("up") || action == "up")
        {
            _inputExecutor.InputService.MoveMouse(packet.X, packet.Y);
            if (_inputExecutor.InputService is GhostDesktopInput.ILocalMouseControl mc)
            {
                mc.MouseUp(btn, packet.X, packet.Y);
            }
        }
        else if (type.Contains("double") || action == "doubleclick")
        {
            _inputExecutor.InputService.Click(packet.X, packet.Y, btn, 2);
        }
        else if (type.Contains("click") || action == "click")
        {
            _inputExecutor.InputService.Click(packet.X, packet.Y, btn, 1);
        }
        else if (type.Contains("wheel") || type.Contains("scroll") || action == "scroll")
        {
            _inputExecutor.InputService.Scroll(packet.X, packet.Y, packet.Delta);
        }
        else if (type.Contains("release") || action == "release_all")
        {
            _inputExecutor.InputService.ReleaseAll();
        }
    }

    private void ExecuteForwardedKeyboard(string payloadJson)
    {
        using var doc = JsonDocument.Parse(payloadJson);
        var root = doc.RootElement;
        string evt = root.TryGetProperty("event", out var evProp) ? evProp.GetString()?.ToLowerInvariant() ?? "" : "";

        // Emergency gate: only key releases pass while stopped.
        if (EmergencyGate.IsStopped && evt != "keyup" && evt != "release")
        {
            return;
        }

        if (evt == "char" && root.TryGetProperty("char", out var charProp))
        {
            string? c = charProp.GetString();
            if (!string.IsNullOrEmpty(c)) _inputExecutor.InputService.TypeText(c);
        }
        else if (evt == "type" && root.TryGetProperty("char", out var textProp))
        {
            string? t = textProp.GetString();
            if (!string.IsNullOrEmpty(t)) _inputExecutor.InputService.TypeText(t);
        }
        else if (evt == "keydown" && root.TryGetProperty("vk", out var vkProp) && vkProp.TryGetInt32(out int vk))
        {
            if (Enum.IsDefined(typeof(KeyCode), vk))
            {
                _inputExecutor.InputService.KeyDown((KeyCode)vk);
            }
        }
        else if (evt == "keyup" && root.TryGetProperty("vk", out var vkUpProp) && vkUpProp.TryGetInt32(out int vkUp))
        {
            if (Enum.IsDefined(typeof(KeyCode), vkUp))
            {
                _inputExecutor.InputService.KeyUp((KeyCode)vkUp);
            }
        }
    }



    private async Task RunConnectLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_isTerminated)
        {
            try
            {
                if (!_ipcClient.IsConnected)
                {
                    // Attempt connection to host pipe
                    await _ipcClient.ConnectAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                }

                // Connected: monitor connection health
                while (_ipcClient.IsConnected && !ct.IsCancellationRequested && !_isTerminated)
                {
                    await Task.Delay(500, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _reconnectCount);
                _lastError = $"IPC connection error: {ex.Message}";

                try
                {
                    await Task.Delay(_config.ReconnectDelayMs, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }



    private bool StartCaptureInternal(int targetFps)
    {
        if (_isCapturing) return true;

        try
        {
            // 1. Initialize capture engine (Prefer DXGI Desktop Duplication, fallback to GDI)
            _captureEngine?.Dispose();
            _captureEngine = ReflectionDxgiCaptureEngine.TryCreate(0, 0);
            if (_captureEngine == null || !_captureEngine.IsInitialized)
            {
                _captureEngine = new GdiDesktopCaptureEngine(_config.DefaultWidth, _config.DefaultHeight);
            }

            // 2. Initialize shared memory writer
            _frameWriter?.Dispose();
            _frameWriter = new NativeGhostFrameBufferWriter(
                _config.SharedBufferMapName,
                _config.SharedEventName);

            if (!_frameWriter.IsInitialized)
            {
                _lastError = "Failed to initialize shared memory frame buffer writer.";
                return false;
            }

            // 3. Start capture thread
            _isCapturing = true;
            _captureThread = new Thread(CaptureLoopWorker)
            {
                Name = "InbriskGhostWorkerCapture",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal
            };
            _captureThread.Start();
            return true;
        }
        catch (Exception ex)
        {
            _lastError = $"StartCapture error: {ex.Message}";
            StopCaptureInternal();
            return false;
        }
    }

    private void StopCaptureInternal()
    {
        _isCapturing = false;

        if (_captureThread != null && _captureThread.IsAlive)
        {
            if (!_captureThread.Join(1500))
            {
                try { _captureThread.Interrupt(); } catch { }
            }
            _captureThread = null;
        }

        _captureEngine?.Dispose();
        _captureEngine = null;

        _frameWriter?.Dispose();
        _frameWriter = null;
    }

    private void CaptureLoopWorker()
    {
        var frameIntervalStopwatch = Stopwatch.StartNew();
        _fpsWatch.Restart();
        _fpsCounter = 0;

        while (_isCapturing && !_isTerminated)
        {
            try
            {
                int frameBudgetMs = Math.Max(1, 1000 / Math.Clamp(_config.TargetFps, 10, 120));

                if (_captureEngine != null && _frameWriter != null)
                {
                    // Acquire frame from GPU / desktop duplication
                    bool acquired = _captureEngine.AcquireNextFrame(
                        timeoutMs: frameBudgetMs,
                        out byte[]? buffer,
                        out int width,
                        out int height,
                        out int stride);

                    if (acquired && buffer != null && width > 0 && height > 0)
                    {
                        try
                        {
                            // Commit to cross-session shared memory ring buffer
                            _frameWriter.WriteFrame(width, height, stride, buffer);

                            Interlocked.Increment(ref _totalFramesCaptured);
                            _fpsCounter++;
                        }
                        finally
                        {
                            _captureEngine.ReleaseFrame();
                        }
                    }

                    // Rolling FPS calculation
                    if (_fpsWatch.ElapsedMilliseconds >= 1000)
                    {
                        double elapsedSec = _fpsWatch.Elapsed.TotalSeconds;
                        Volatile.Write(ref _currentFps, _fpsCounter / elapsedSec);
                        _fpsCounter = 0;
                        _fpsWatch.Restart();
                    }
                }

                // Pacing throttle for target FPS budget
                long elapsed = frameIntervalStopwatch.ElapsedMilliseconds;
                if (elapsed < frameBudgetMs)
                {
                    int sleepMs = (int)(frameBudgetMs - elapsed);
                    if (sleepMs > 1)
                    {
                        Thread.Sleep(sleepMs);
                    }
                }
                frameIntervalStopwatch.Restart();
            }
            catch (ThreadInterruptedException)
            {
                break;
            }
            catch (Exception ex)
            {
                _lastError = $"Capture frame error: {ex.Message}";
                Thread.Sleep(30);
            }
        }
    }



    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        StopCaptureInternal();

        _cts?.Cancel();
        _cts?.Dispose();

        _ipcClient.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        StopCaptureInternal();

        _cts?.Cancel();
        _cts?.Dispose();

        await _ipcClient.DisposeAsync().ConfigureAwait(false);
    }

}


