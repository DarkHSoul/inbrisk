using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Inbrisk.Mcp.Daemon;

/// <summary>
/// Thin Stdio Proxy for Inbrisk MCP.
/// Translates MCP stdio requests to/from the persistent runtime daemon via Named Pipe IPC.
/// Provides race-safe daemon startup, protocol version negotiation, in-flight request tracking,
/// truthful mutation failure on disconnection (no silent replay), and handle invalidation across epochs.
/// </summary>
public sealed class ThinStdioProxy : IAsyncDisposable, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public int ClientProtocolVersion { get; set; } = InbriskRuntimeDaemon.CurrentProtocolVersion;
    public string PipeName { get; }
    public string MutexName { get; }
    public string? SessionId { get; private set; }
    public long SessionEpoch { get; private set; }
    public bool IsConnected => _pipeClient?.IsConnected == true;

    private NamedPipeClientStream? _pipeClient;
    private readonly ConcurrentDictionary<string, InFlightRequest> _inFlightRequests = new();
    private readonly ConcurrentDictionary<string, SessionHandle> _trackedHandles = new();

    private CancellationTokenSource? _readerCts;
    private Task? _pipeReaderTask;
    private TextWriter? _stdioWriter;
    private readonly object _writeGate = new();
    private bool _disposed;

    public IReadOnlyCollection<InFlightRequest> InFlightRequests => _inFlightRequests.Values.ToList().AsReadOnly();
    public int DaemonPid { get; private set; }

    public ThinStdioProxy(string? pipeName = null, string? mutexName = null)
    {
        PipeName = pipeName ?? InbriskRuntimeDaemon.GetDefaultPipeName();
        MutexName = mutexName ?? InbriskRuntimeDaemon.GetDefaultMutexName();
    }

    public static bool IsMutatingTool(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return false;
        return toolName is "computer_batch"
            or "computer_do"
            or "computer_run"
            or "computer_click"
            or "computer_type"
            or "computer_hotkey"
            or "computer_close_window"
            or "computer_drag"
            or "computer_scroll"
            or "computer_scroll_into_view"
            or "computer_set_value"
            or "computer_invoke"
            or "computer_key"
            or "computer_launch"
            or "computer_focus_window"
            or "computer_reset_input"
            or "computer_ui_set"
            or "computer_app_restart"
            or "computer_app_shutdown"
            or "computer_cancel_task"
            or "browser_click"
            or "browser_type"
            or "browser_browse";
    }

    public SessionHandle RegisterHandle(string handleId)
    {
        var handle = new SessionHandle(handleId, SessionEpoch, SessionId ?? string.Empty);
        _trackedHandles[handleId] = handle;
        return handle;
    }

    public bool IsValidHandle(SessionHandle handle)
    {
        if (handle == null) return false;
        return handle.Epoch == SessionEpoch &&
               handle.SessionId == SessionId &&
               !string.IsNullOrEmpty(SessionId);
    }

    public bool IsValidHandle(string handleId)
    {
        if (string.IsNullOrWhiteSpace(handleId)) return false;
        return _trackedHandles.TryGetValue(handleId, out var h) && IsValidHandle(h);
    }

    public async Task ConnectOrStartDaemonAsync(Func<Task<InbriskRuntimeDaemon>> daemonStarter, CancellationToken ct = default)
    {
        if (await InbriskRuntimeDaemon.CanConnectToPipeAsync(PipeName, TimeSpan.FromMilliseconds(50), ct).ConfigureAwait(false))
        {
            await ConnectAsync(ct).ConfigureAwait(false);
            return;
        }

        Mutex? mutex = null;
        bool isCreator = false;
        bool hasLock = false;
        try
        {
            try
            {
                mutex = new Mutex(true, MutexName, out bool createdNew);
                isCreator = createdNew;
                hasLock = createdNew;
            }
            catch (UnauthorizedAccessException)
            {
                var localName = MutexName.Replace(@"Global\", @"Local\");
                mutex = new Mutex(true, localName, out bool createdNew);
                isCreator = createdNew;
                hasLock = createdNew;
            }

            if (!hasLock)
            {
                try
                {
                    hasLock = mutex.WaitOne(TimeSpan.FromSeconds(10));
                }
                catch (AbandonedMutexException)
                {
                    hasLock = true;
                }
            }

            if (isCreator)
            {
                if (!await InbriskRuntimeDaemon.CanConnectToPipeAsync(PipeName, TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false))
                {
                    await daemonStarter().ConfigureAwait(false);
                }
            }
            else
            {
                await InbriskRuntimeDaemon.WaitForPipeAvailableAsync(PipeName, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            }
        }
        finally
        {
            if (hasLock && mutex != null)
            {
                try { mutex.ReleaseMutex(); } catch { }
            }
        }

        await ConnectAsync(ct).ConfigureAwait(false);
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (IsConnected) return;

        const int maxAttempts = 5;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                await client.ConnectAsync(linked.Token).ConfigureAwait(false);

                // 1. Handshake Phase
                var handshakeReq = new DaemonHandshakeRequest
                {
                    ProtocolVersion = ClientProtocolVersion,
                    ClientName = "thin-stdio-proxy",
                    ClientVersion = "0.5.0"
                };

                await WriteLineAsync(client, JsonSerializer.Serialize(handshakeReq, JsonOptions), linked.Token).ConfigureAwait(false);

                var responseLine = await ReadLineByteByByteAsync(client, linked.Token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(responseLine))
                {
                    if (attempt < maxAttempts)
                    {
                        client.Dispose();
                        await Task.Delay(50 * attempt, ct).ConfigureAwait(false);
                        continue;
                    }
                    throw new IOException("Failed to receive handshake response from runtime daemon.");
                }

                var handshakeResp = JsonSerializer.Deserialize<DaemonHandshakeResponse>(responseLine, JsonOptions);
                if (handshakeResp == null)
                {
                    throw new IOException("Invalid handshake response received from runtime daemon.");
                }

                if (!handshakeResp.Success || handshakeResp.ProtocolVersion != ClientProtocolVersion)
                {
                    throw new ProtocolVersionMismatchException(
                        ClientProtocolVersion,
                        handshakeResp.ProtocolVersion,
                        handshakeResp.Error);
                }

                SessionId = handshakeResp.SessionId;
                SessionEpoch = handshakeResp.SessionEpoch;
                DaemonPid = handshakeResp.DaemonPid;
                _pipeClient = client;

                // Start background pipe reading loop
                _readerCts = new CancellationTokenSource();
                _pipeReaderTask = Task.Run(() => ReadFromPipeLoopAsync(_readerCts.Token));
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts && (ex is IOException || ex is TimeoutException))
            {
                client.Dispose();
                await Task.Delay(50 * attempt, ct).ConfigureAwait(false);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }
    }

    public async Task ReconnectAsync(CancellationToken ct = default)
    {
        await DisconnectAsync().ConfigureAwait(false);
        await ConnectAsync(ct).ConfigureAwait(false);
    }

    public Task DisconnectAsync()
    {
        try { _readerCts?.Cancel(); } catch { }
        try { _pipeClient?.Dispose(); } catch { }
        _pipeClient = null;

        // Truthfully fail all in-flight requests on disconnect without replaying
        FailInFlightRequestsTruthfully();
        return Task.CompletedTask;
    }

    private void FailInFlightRequestsTruthfully()
    {
        var inFlight = _inFlightRequests.Values.ToList();
        _inFlightRequests.Clear();

        foreach (var req in inFlight)
        {
            if (req.IsMutating)
            {
                var ex = new InFlightMutationFailedException(
                    "Connection to runtime daemon was lost while mutating request was in-flight. Request was not replayed to avoid duplicate execution.",
                    req.Id,
                    req.ToolName);
                req.CompletionSource?.TrySetException(ex);

                if (_stdioWriter != null)
                {
                    var errorJson = JsonSerializer.Serialize(new
                    {
                        jsonrpc = "2.0",
                        id = req.Id,
                        error = new
                        {
                            code = -32000,
                            message = "Connection to runtime daemon was lost while mutating request was in-flight. Request was not replayed to avoid duplicate execution."
                        }
                    });
                    try
                    {
                        lock (_writeGate)
                        {
                            _stdioWriter.WriteLine(errorJson);
                            _stdioWriter.Flush();
                        }
                    }
                    catch { }
                }
            }
            else
            {
                var ex = new IOException("Connection to runtime daemon was lost while request was in-flight.");
                req.CompletionSource?.TrySetException(ex);

                if (_stdioWriter != null)
                {
                    var errorJson = JsonSerializer.Serialize(new
                    {
                        jsonrpc = "2.0",
                        id = req.Id,
                        error = new
                        {
                            code = -32000,
                            message = "Connection to runtime daemon was lost while request was in-flight."
                        }
                    });
                    try
                    {
                        lock (_writeGate)
                        {
                            _stdioWriter.WriteLine(errorJson);
                            _stdioWriter.Flush();
                        }
                    }
                    catch { }
                }
            }
        }
    }

    private async Task ReadFromPipeLoopAsync(CancellationToken ct)
    {
        if (_pipeClient == null) return;
        using var reader = new StreamReader(_pipeClient, Encoding.UTF8, leaveOpen: true);

        try
        {
            while (!ct.IsCancellationRequested && _pipeClient.IsConnected)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line == null) break; // EOF: daemon severed or crashed

                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;

                // Inspect JSON-RPC response
                try
                {
                    using var doc = JsonDocument.Parse(trimmed);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("id", out var idElem))
                    {
                        var idKey = idElem.ToString();
                        if (_inFlightRequests.TryRemove(idKey, out var inFlight))
                        {
                            inFlight.CompletionSource?.TrySetResult(trimmed);
                        }
                    }
                }
                catch
                {
                    // Non-JSON frame ignored in parsing, forwarded to stdio if present
                }

                if (_stdioWriter != null)
                {
                    lock (_writeGate)
                    {
                        _stdioWriter.WriteLine(trimmed);
                        _stdioWriter.Flush();
                    }
                }
            }
        }
        catch
        {
            // Pipe disconnected or aborted
        }
        finally
        {
            FailInFlightRequestsTruthfully();
        }
    }

    public async Task<string> SendRawRequestAsync(string jsonRpcRequest, CancellationToken ct = default)
    {
        if (!IsConnected)
        {
            await ConnectAsync(ct).ConfigureAwait(false);
        }

        object id = Guid.NewGuid().ToString("N");
        string method = "unknown";
        string? toolName = null;

        try
        {
            using var doc = JsonDocument.Parse(jsonRpcRequest);
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var idElem))
            {
                id = idElem.ValueKind == JsonValueKind.Number ? (object)idElem.GetInt64() : idElem.GetString() ?? id;
            }
            if (root.TryGetProperty("method", out var mElem))
            {
                method = mElem.GetString() ?? "unknown";
            }
            if (method == "tools/call" && root.TryGetProperty("params", out var pElem) && pElem.TryGetProperty("name", out var nElem))
            {
                toolName = nElem.GetString();
            }
        }
        catch { }

        bool isMutating = IsMutatingTool(toolName);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = new InFlightRequest(id, method, toolName, isMutating, tcs);
        _inFlightRequests[id.ToString()!] = inFlight;

        try
        {
            await WriteLineAsync(_pipeClient!, jsonRpcRequest, ct).ConfigureAwait(false);
            using var registration = ct.Register(() => tcs.TrySetCanceled());
            return await tcs.Task.ConfigureAwait(false);
        }
        catch when (isMutating && !tcs.Task.IsCompletedSuccessfully)
        {
            throw new InFlightMutationFailedException(
                "Connection to runtime daemon was lost while mutating request was in-flight. Request was not replayed to avoid duplicate execution.",
                id,
                toolName);
        }
    }

    public async Task RunStdioAsync(
        TextReader stdin,
        TextWriter stdout,
        TextWriter stderr,
        int? maxFrames = null,
        CancellationToken ct = default)
    {
        _stdioWriter = stdout;
        if (!IsConnected)
        {
            await ConnectAsync(ct).ConfigureAwait(false);
        }

        int framesProcessed = 0;
        while (!ct.IsCancellationRequested && IsConnected)
        {
            var line = await stdin.ReadLineAsync(ct).ConfigureAwait(false);
            if (line == null) break;

            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            object id = Guid.NewGuid().ToString("N");
            string method = "unknown";
            string? toolName = null;

            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;
                if (root.TryGetProperty("id", out var idElem))
                {
                    id = idElem.ValueKind == JsonValueKind.Number ? (object)idElem.GetInt64() : idElem.GetString() ?? id;
                }
                if (root.TryGetProperty("method", out var mElem))
                {
                    method = mElem.GetString() ?? "unknown";
                }
                if (method == "tools/call" && root.TryGetProperty("params", out var pElem) && pElem.TryGetProperty("name", out var nElem))
                {
                    toolName = nElem.GetString();
                }
            }
            catch (Exception ex)
            {
                await stderr.WriteLineAsync($"[Proxy Error] Malformed JSON-RPC input: {ex.Message}").ConfigureAwait(false);
                continue;
            }

            bool isMutating = IsMutatingTool(toolName);
            var inFlight = new InFlightRequest(id, method, toolName, isMutating);
            _inFlightRequests[id.ToString()!] = inFlight;

            await WriteLineAsync(_pipeClient!, trimmed, ct).ConfigureAwait(false);

            framesProcessed++;
            if (maxFrames.HasValue && framesProcessed >= maxFrames.Value)
            {
                // Wait for responses to flush to stdout before terminating
                var flushSw = System.Diagnostics.Stopwatch.StartNew();
                while (_inFlightRequests.Count > 0 && flushSw.ElapsedMilliseconds < 3000)
                {
                    await Task.Delay(20, ct).ConfigureAwait(false);
                }
                // Brief final yield to allow writer to flush to stdout
                await Task.Delay(50, ct).ConfigureAwait(false);
                break;
            }
        }
    }

    public static async Task<int> RunAsync(string[] args)
    {
        // Support in-process fallback or direct mode, including isolated test harnesses with custom emergency state
        if (args.Contains("--direct", StringComparer.OrdinalIgnoreCase) ||
            args.Contains("--in-process", StringComparer.OrdinalIgnoreCase) ||
            string.Equals(Environment.GetEnvironmentVariable("INBRISK_MCP_DIRECT"), "1", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("INBRISK_EMERGENCY_STATE")))
        {
            return await McpHost.RunAsync(args).ConfigureAwait(false);
        }

        // Support explicit daemon mode
        if (args.Contains("--daemon", StringComparer.OrdinalIgnoreCase))
        {
            await using var daemon = new InbriskRuntimeDaemon();
            await daemon.StartAsync().ConfigureAwait(false);
            var tcs = new TaskCompletionSource();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => tcs.TrySetResult();
            await tcs.Task.ConfigureAwait(false);
            return 0;
        }

        // Standard thin proxy mode
        var proxy = new ThinStdioProxy();
        try
        {
            await proxy.ConnectOrStartDaemonAsync(async () =>
            {
                var d = new InbriskRuntimeDaemon();
                await d.StartAsync().ConfigureAwait(false);
                return d;
            }).ConfigureAwait(false);

            await proxy.RunStdioAsync(Console.In, Console.Out, Console.Error).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[Inbrisk Proxy Fallback] Daemon connection failed ({ex.Message}), falling back to direct mode.").ConfigureAwait(false);
            return await McpHost.RunAsync(args).ConfigureAwait(false);
        }
        finally
        {
            await proxy.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<string?> ReadLineByteByByteAsync(Stream stream, CancellationToken ct)
    {
        var ms = new MemoryStream();
        var buffer = new byte[1];

        while (!ct.IsCancellationRequested)
        {
            int bytesRead = await stream.ReadAsync(buffer, 0, 1, ct).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                if (ms.Length == 0) return null;
                break;
            }

            if (buffer[0] == '\n') break;
            if (buffer[0] != '\r') ms.WriteByte(buffer[0]);
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static async Task WriteLineAsync(Stream stream, string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text + "\n");
        await stream.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _readerCts?.Cancel(); } catch { }
        try { _readerCts?.Dispose(); } catch { }
        try { _pipeClient?.Dispose(); } catch { }
        _pipeClient = null;
        FailInFlightRequestsTruthfully();
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        Dispose();
    }
}
