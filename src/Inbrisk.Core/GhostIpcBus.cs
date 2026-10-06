using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Inbrisk.Core;

/// <summary>
/// Common defaults, constants, and factory helpers for cross-session Ghost IPC.
/// </summary>
public static class GhostIpcDefaults
{
    /// <summary>
    /// Default pipe name used for cross-session communication between Session 1 (Host/MCP)
    /// and Session 2 (InbriskAgent worker).
    /// </summary>
    public const string PipeName = "inbrisk_ghost_bus";

    /// <summary>
    /// Maximum allowed packet payload size (64 MB) to prevent out-of-memory errors on corrupt streams.
    /// </summary>
    public const int MaxPayloadBytes = 64 * 1024 * 1024;

    /// <summary>
    /// Default request timeout for IPC operations.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Creates a PipeSecurity instance granting Read/Write access to Everyone (World),
    /// Authenticated Users, Administrators, and SYSTEM. This allows Session 2 (InbriskAgent)
    /// running under another user account to connect to Session 1 without permission errors.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static PipeSecurity CreateCrossSessionPipeSecurity()
    {
        var ps = new PipeSecurity();

        // Everyone / World SID (S-1-1-0)
        ps.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));

        // Authenticated Users SID (S-1-5-11)
        ps.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));

        // Built-in Administrators SID (S-1-5-32-544)
        ps.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        // Local SYSTEM SID (S-1-5-18)
        ps.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        // Current User identity
        try
        {
            var currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser != null)
            {
                ps.AddAccessRule(new PipeAccessRule(
                    currentUser,
                    PipeAccessRights.FullControl,
                    AccessControlType.Allow));
            }
        }
        catch
        {
            // Fallback if token inspection is restricted
        }

        return ps;
    }

    /// <summary>
    /// Creates a NamedPipeServerStream configured for cross-session access.
    /// </summary>
    public static NamedPipeServerStream CreateServerStream(
        string pipeName = PipeName,
        int maxInstances = NamedPipeServerStream.MaxAllowedServerInstances,
        int bufferSize = 65536)
    {
        if (OperatingSystem.IsWindows())
        {
            var security = CreateCrossSessionPipeSecurity();
            return NamedPipeServerStreamAcl.Create(
                pipeName,
                PipeDirection.InOut,
                maxInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                inBufferSize: bufferSize,
                outBufferSize: bufferSize,
                pipeSecurity: security);
        }

        return new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: bufferSize,
            outBufferSize: bufferSize);
    }

    /// <summary>
    /// Creates a NamedPipeClientStream connecting to the specified pipe.
    /// </summary>
    public static NamedPipeClientStream CreateClientStream(
        string pipeName = PipeName,
        string serverName = ".")
    {
        return new NamedPipeClientStream(
            serverName,
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
    }
}

/// <summary>
/// Envelope representing framed messages over the Ghost IPC bus.
/// </summary>
public sealed record GhostIpcEnvelope
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Type { get; init; } = "request"; // "request", "response", "ping", "pong"
    public string Action { get; init; } = string.Empty;
    public string? CorrelationId { get; init; }
    public string? Payload { get; init; }
    public string? Error { get; init; }
    public long TimestampTicks { get; init; } = DateTime.UtcNow.Ticks;

    public GhostIpcMessage ToContractMessage() =>
        new(Id, Action, Payload ?? string.Empty, TimestampTicks);
}

/// <summary>
/// Exception thrown when an IPC operation fails or returns an error.
/// </summary>
public sealed class GhostIpcException : Exception
{
    public GhostIpcException(string message) : base(message) { }
    public GhostIpcException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Core contract for bidirectional Ghost IPC communication.
/// </summary>
public interface IGhostIpcBus : IAsyncDisposable, IDisposable
{
    /// <summary>Whether the IPC channel is currently connected and active.</summary>
    bool IsConnected { get; }

    /// <summary>Registers an asynchronous request handler for the specified action.</summary>
    void RegisterHandler(string action, Func<string, Task<string>> handler);

    /// <summary>Sends a request with raw JSON/string payload and awaits the response payload.</summary>
    Task<string> SendRequestAsync(string action, string payloadJson, TimeSpan timeout, CancellationToken ct = default);

    /// <summary>Sends a typed request and deserializes the response.</summary>
    Task<TResp?> SendRequestAsync<TReq, TResp>(string action, TReq payload, TimeSpan timeout, CancellationToken ct = default);

    /// <summary>Sends a ping frame and awaits a pong response.</summary>
    Task<bool> PingAsync(TimeSpan timeout, CancellationToken ct = default);
}

/// <summary>
/// Manages high-speed length-prefixed duplex communication over a connected PipeStream.
/// </summary>
public sealed class GhostIpcConnection : IGhostIpcBus
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private readonly PipeStream _stream;
    private readonly ConcurrentDictionary<string, Func<string, Task<string>>> _handlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<GhostIpcEnvelope>> _pendingRequests = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<bool> _completionTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _disposed;

    public bool IsConnected => _stream.IsConnected && _disposed == 0 && !_cts.IsCancellationRequested;

    public Task Completion => _completionTcs.Task;

    public GhostIpcConnection(PipeStream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _ = Task.Run(ReadLoopAsync);
    }

    public void RegisterHandler(string action, Func<string, Task<string>> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentNullException.ThrowIfNull(handler);
        _handlers[action] = handler;
    }

    public async Task<string> SendRequestAsync(string action, string payloadJson, TimeSpan timeout, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (!_stream.IsConnected)
            throw new GhostIpcException("IPC pipe stream is disconnected.");

        var envelope = new GhostIpcEnvelope
        {
            Type = "request",
            Action = action,
            Payload = payloadJson
        };

        var resp = await SendEnvelopeAndWaitResponseAsync(envelope, timeout, ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(resp.Error))
        {
            throw new GhostIpcException(resp.Error);
        }

        return resp.Payload ?? string.Empty;
    }

    public async Task<TResp?> SendRequestAsync<TReq, TResp>(string action, TReq payload, TimeSpan timeout, CancellationToken ct = default)
    {
        string payloadJson = payload is string str ? str : JsonSerializer.Serialize(payload, JsonOptions);
        string responseJson = await SendRequestAsync(action, payloadJson, timeout, ct).ConfigureAwait(false);

        if (typeof(TResp) == typeof(string))
        {
            return (TResp?)(object)responseJson;
        }

        if (string.IsNullOrWhiteSpace(responseJson))
        {
            return default;
        }

        return JsonSerializer.Deserialize<TResp>(responseJson, JsonOptions);
    }

    public async Task<bool> PingAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        if (!IsConnected) return false;

        try
        {
            var req = new GhostIpcEnvelope
            {
                Type = "ping",
                Action = "__ping",
                Payload = "ping"
            };

            var resp = await SendEnvelopeAndWaitResponseAsync(req, timeout, ct).ConfigureAwait(false);
            return resp.Type == "pong" || resp.Action == "__pong" || resp.Payload == "pong";
        }
        catch
        {
            return false;
        }
    }

    private async Task<GhostIpcEnvelope> SendEnvelopeAndWaitResponseAsync(GhostIpcEnvelope envelope, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<GhostIpcEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[envelope.Id] = tcs;

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token, _cts.Token);
        using var reg = linkedCts.Token.Register(() =>
        {
            if (_pendingRequests.TryRemove(envelope.Id, out var pending))
            {
                if (_cts.IsCancellationRequested || !IsConnected)
                    pending.TrySetException(new GhostIpcException("IPC connection was closed."));
                else if (ct.IsCancellationRequested)
                    pending.TrySetCanceled(ct);
                else
                    pending.TrySetException(new TimeoutException($"IPC request '{envelope.Action}' timed out after {timeout.TotalMilliseconds:F0}ms."));
            }
        });

        await SendEnvelopeAsync(envelope, linkedCts.Token).ConfigureAwait(false);
        return await tcs.Task.ConfigureAwait(false);
    }

    private async Task SendEnvelopeAsync(GhostIpcEnvelope envelope, CancellationToken ct)
    {
        string json = JsonSerializer.Serialize(envelope, JsonOptions);
        byte[] bodyBytes = Encoding.UTF8.GetBytes(json);

        byte[] lengthBytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, bodyBytes.Length);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(lengthBytes, ct).ConfigureAwait(false);
            await _stream.WriteAsync(bodyBytes, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
            GhostTelemetry.RecordIpcSent();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        byte[] lengthBuf = new byte[4];

        try
        {
            while (!_cts.IsCancellationRequested && _stream.IsConnected)
            {
                await ReadExactAsync(_stream, lengthBuf, 0, 4, _cts.Token).ConfigureAwait(false);
                int length = BinaryPrimitives.ReadInt32LittleEndian(lengthBuf);

                if (length <= 0 || length > GhostIpcDefaults.MaxPayloadBytes)
                {
                    throw new InvalidDataException($"Invalid Ghost IPC frame length: {length}");
                }

                byte[] bodyBuf = new byte[length];
                await ReadExactAsync(_stream, bodyBuf, 0, length, _cts.Token).ConfigureAwait(false);
                GhostTelemetry.RecordIpcReceived();

                string json = Encoding.UTF8.GetString(bodyBuf);
                var envelope = JsonSerializer.Deserialize<GhostIpcEnvelope>(json, JsonOptions);
                if (envelope == null) continue;

                DispatchEnvelope(envelope);
            }
        }
        catch
        {
            // Expected on stream disconnect or abort
        }
        finally
        {
            FaultAllPendingRequests("IPC connection was closed.");
            _completionTcs.TrySetResult(true);
        }
    }

    private void DispatchEnvelope(GhostIpcEnvelope envelope)
    {
        // 1. Response or Pong
        if (envelope.Type == "response" || envelope.Type == "pong" || envelope.Action == "__pong")
        {
            string? correlationId = envelope.CorrelationId ?? envelope.Id;
            if (!string.IsNullOrEmpty(correlationId) && _pendingRequests.TryRemove(correlationId, out var tcs))
            {
                tcs.TrySetResult(envelope);
            }
            return;
        }

        // 2. Ping
        if (envelope.Type == "ping" || envelope.Action == "__ping")
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var pong = new GhostIpcEnvelope
                    {
                        Type = "pong",
                        CorrelationId = envelope.Id,
                        Action = "__pong",
                        Payload = "pong"
                    };
                    await SendEnvelopeAsync(pong, CancellationToken.None).ConfigureAwait(false);
                }
                catch { }
            });
            return;
        }

        // 3. Request
        if (envelope.Type == "request")
        {
            _ = Task.Run(async () =>
            {
                GhostIpcEnvelope responseEnvelope;
                if (_handlers.TryGetValue(envelope.Action, out var handler))
                {
                    try
                    {
                        string result = await handler(envelope.Payload ?? string.Empty).ConfigureAwait(false);
                        responseEnvelope = new GhostIpcEnvelope
                        {
                            Type = "response",
                            CorrelationId = envelope.Id,
                            Action = envelope.Action,
                            Payload = result
                        };
                    }
                    catch (Exception ex)
                    {
                        responseEnvelope = new GhostIpcEnvelope
                        {
                            Type = "response",
                            CorrelationId = envelope.Id,
                            Action = envelope.Action,
                            Error = ex.Message
                        };
                    }
                }
                else
                {
                    responseEnvelope = new GhostIpcEnvelope
                    {
                        Type = "response",
                        CorrelationId = envelope.Id,
                        Action = envelope.Action,
                        Error = $"No handler registered for action '{envelope.Action}'."
                    };
                }

                try
                {
                    await SendEnvelopeAsync(responseEnvelope, CancellationToken.None).ConfigureAwait(false);
                }
                catch { }
            });
        }
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset + totalRead, count - totalRead), ct).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("Ghost IPC pipe stream ended prematurely.");
            }
            totalRead += read;
        }
    }

    private void FaultAllPendingRequests(string reason)
    {
        foreach (var key in _pendingRequests.Keys)
        {
            if (_pendingRequests.TryRemove(key, out var pending))
            {
                pending.TrySetException(new GhostIpcException(reason));
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();
        _cts.Dispose();
        _writeLock.Dispose();
        _stream.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();
        _cts.Dispose();
        _writeLock.Dispose();
        await _stream.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Server-side Named Pipe listener (typically running in Session 1 or host daemon).
/// Listens on `inbrisk_ghost_bus` with cross-session PipeSecurity, accepting connections from Session 2.
/// </summary>
public sealed class GhostIpcServer : IGhostIpcBus
{
    private readonly string _pipeName;
    private readonly ConcurrentDictionary<string, Func<string, Task<string>>> _handlers = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private GhostIpcConnection? _activeConnection;
    private int _disposed;

    public event EventHandler? ClientConnected;
    public event EventHandler? ClientDisconnected;

    public string PipeName => _pipeName;

    public bool IsConnected => _activeConnection?.IsConnected == true;

    public GhostIpcServer(string pipeName = GhostIpcDefaults.PipeName)
    {
        _pipeName = pipeName;
    }

    /// <summary>
    /// Starts the background listener loop accepting incoming connections.
    /// </summary>
    public Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (_listenTask != null) return Task.CompletedTask;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _listenTask = ListenLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public void RegisterHandler(string action, Func<string, Task<string>> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentNullException.ThrowIfNull(handler);

        _handlers[action] = handler;
        _activeConnection?.RegisterHandler(action, handler);
    }

    public Task<string> SendRequestAsync(string action, string payloadJson, TimeSpan timeout, CancellationToken ct = default)
    {
        var conn = _activeConnection;
        if (conn == null || !conn.IsConnected)
            throw new GhostIpcException("No active Ghost IPC client connection on server.");

        return conn.SendRequestAsync(action, payloadJson, timeout, ct);
    }

    public Task<TResp?> SendRequestAsync<TReq, TResp>(string action, TReq payload, TimeSpan timeout, CancellationToken ct = default)
    {
        var conn = _activeConnection;
        if (conn == null || !conn.IsConnected)
            throw new GhostIpcException("No active Ghost IPC client connection on server.");

        return conn.SendRequestAsync<TReq, TResp>(action, payload, timeout, ct);
    }

    public Task<bool> PingAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var conn = _activeConnection;
        if (conn == null || !conn.IsConnected)
            return Task.FromResult(false);

        return conn.PingAsync(timeout, ct);
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _disposed == 0)
        {
            NamedPipeServerStream? serverStream = null;
            try
            {
                serverStream = GhostIpcDefaults.CreateServerStream(_pipeName);
                await serverStream.WaitForConnectionAsync(ct).ConfigureAwait(false);

                var connection = new GhostIpcConnection(serverStream);
                foreach (var kvp in _handlers)
                {
                    connection.RegisterHandler(kvp.Key, kvp.Value);
                }

                _activeConnection = connection;
                ClientConnected?.Invoke(this, EventArgs.Empty);

                await connection.Completion.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Delay before retrying accept
                try
                {
                    await Task.Delay(250, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            finally
            {
                if (_activeConnection != null)
                {
                    _activeConnection = null;
                    ClientDisconnected?.Invoke(this, EventArgs.Empty);
                }

                if (serverStream != null)
                {
                    try { serverStream.Dispose(); } catch { }
                }
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts?.Cancel();
        _cts?.Dispose();
        _activeConnection?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts?.Cancel();
        _cts?.Dispose();
        if (_activeConnection != null)
        {
            await _activeConnection.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Client-side Named Pipe worker endpoint (typically running in Session 2).
/// Connects to `\\.\pipe\inbrisk_ghost_bus` and communicates with the host session.
/// </summary>
public sealed class GhostIpcClient : IGhostIpcBus
{
    private readonly string _pipeName;
    private readonly string _serverName;
    private readonly ConcurrentDictionary<string, Func<string, Task<string>>> _handlers = new(StringComparer.OrdinalIgnoreCase);

    private GhostIpcConnection? _connection;
    private CancellationTokenSource? _heartbeatCts;
    private int _disposed;

    public event EventHandler? Disconnected;

    public string PipeName => _pipeName;

    public bool IsConnected => _connection?.IsConnected == true;

    public GhostIpcClient(string pipeName = GhostIpcDefaults.PipeName, string serverName = ".")
    {
        _pipeName = pipeName;
        _serverName = serverName;
    }

    /// <summary>
    /// Connects to the host Ghost IPC server named pipe with timeout and retry logic.
    /// </summary>
    public async Task ConnectAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (IsConnected) return;

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        NamedPipeClientStream? clientStream = null;
        var start = DateTime.UtcNow;

        while (!linkedCts.Token.IsCancellationRequested)
        {
            try
            {
                clientStream = GhostIpcDefaults.CreateClientStream(_pipeName, _serverName);
                await clientStream.ConnectAsync(linkedCts.Token).ConfigureAwait(false);

                var connection = new GhostIpcConnection(clientStream);
                foreach (var kvp in _handlers)
                {
                    connection.RegisterHandler(kvp.Key, kvp.Value);
                }

                _ = connection.Completion.ContinueWith(_ =>
                {
                    Disconnected?.Invoke(this, EventArgs.Empty);
                }, TaskScheduler.Default);

                _connection = connection;
                return;
            }
            catch (OperationCanceledException) when (linkedCts.Token.IsCancellationRequested)
            {
                clientStream?.Dispose();
                throw new TimeoutException($"Timed out connecting to Ghost IPC pipe '{_pipeName}' after {(DateTime.UtcNow - start).TotalMilliseconds:F0}ms.");
            }
            catch
            {
                clientStream?.Dispose();
                clientStream = null;
                // Retry backoff
                await Task.Delay(100, linkedCts.Token).ConfigureAwait(false);
            }
        }

        throw new TimeoutException($"Could not connect to Ghost IPC pipe '{_pipeName}'.");
    }

    public void RegisterHandler(string action, Func<string, Task<string>> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentNullException.ThrowIfNull(handler);

        _handlers[action] = handler;
        _connection?.RegisterHandler(action, handler);
    }

    public Task<string> SendRequestAsync(string action, string payloadJson, TimeSpan timeout, CancellationToken ct = default)
    {
        var conn = _connection;
        if (conn == null || !conn.IsConnected)
            throw new GhostIpcException("Ghost IPC client is not connected.");

        return conn.SendRequestAsync(action, payloadJson, timeout, ct);
    }

    public Task<TResp?> SendRequestAsync<TReq, TResp>(string action, TReq payload, TimeSpan timeout, CancellationToken ct = default)
    {
        var conn = _connection;
        if (conn == null || !conn.IsConnected)
            throw new GhostIpcException("Ghost IPC client is not connected.");

        return conn.SendRequestAsync<TReq, TResp>(action, payload, timeout, ct);
    }

    public Task<bool> PingAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var conn = _connection;
        if (conn == null || !conn.IsConnected)
            return Task.FromResult(false);

        return conn.PingAsync(timeout, ct);
    }

    /// <summary>
    /// Starts a background heartbeat that periodically pings the server.
    /// If ping fails consecutive times, onFailed is invoked.
    /// </summary>
    public void StartHeartbeat(TimeSpan interval, TimeSpan timeout, Action<Exception>? onFailed = null)
    {
        StopHeartbeat();
        _heartbeatCts = new CancellationTokenSource();
        var ct = _heartbeatCts.Token;

        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested && IsConnected)
            {
                try
                {
                    await Task.Delay(interval, ct).ConfigureAwait(false);
                    bool ok = await PingAsync(timeout, ct).ConfigureAwait(false);
                    if (!ok)
                    {
                        onFailed?.Invoke(new GhostIpcException("Heartbeat ping response missing or invalid."));
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    onFailed?.Invoke(ex);
                }
            }
        }, ct);
    }

    /// <summary>
    /// Stops any active background heartbeat.
    /// </summary>
    public void StopHeartbeat()
    {
        _heartbeatCts?.Cancel();
        _heartbeatCts?.Dispose();
        _heartbeatCts = null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        StopHeartbeat();
        _connection?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        StopHeartbeat();
        if (_connection != null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
