using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Inbrisk.Core;
using Inbrisk.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Protocol;

namespace Inbrisk.Mcp.Daemon;

/// <summary>
/// Persistent single-instance per-user runtime daemon for Inbrisk.
/// Serves multiple thin proxies over Windows Named Pipes with session isolation
/// and a shared authoritative DesktopArbiter.
/// </summary>
public sealed class InbriskRuntimeDaemon : IAsyncDisposable, IDisposable
{
    public const int CurrentProtocolVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public string PipeName { get; }
    public string MutexName { get; }
    public DesktopArbiter Arbiter { get; }
    public EmergencyControl Control { get; }
    public long Epoch { get; }
    public bool IsRunning { get; private set; }
    public bool IsDisposed { get; private set; }
    public int ConnectedSessionCount => _activeSessions.Count;

    private readonly Func<InbriskRuntimeDaemon, McpSession>? _sessionFactory;
    private readonly bool _startEvents;
    private readonly string _toolProfile;
    private readonly bool _ownsArbiter;
    private readonly bool _ownsControl;

    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _startedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _listenerTask;
    private readonly ConcurrentDictionary<string, McpSession> _activeSessions = new();
    private readonly ConcurrentDictionary<string, Stream> _activeStreams = new();

    public InbriskRuntimeDaemon(
        string? pipeName = null,
        string? mutexName = null,
        DesktopArbiter? arbiter = null,
        EmergencyControl? control = null,
        long epoch = 1,
        Func<InbriskRuntimeDaemon, McpSession>? sessionFactory = null,
        bool startEvents = true,
        string? toolProfile = null)
    {
        PipeName = pipeName ?? GetDefaultPipeName();
        MutexName = mutexName ?? GetDefaultMutexName();
        Epoch = epoch;
        _sessionFactory = sessionFactory;
        _startEvents = startEvents;
        _toolProfile = toolProfile ?? Environment.GetEnvironmentVariable("INBRISK_TOOL_PROFILE") ?? "full";

        if (arbiter != null)
        {
            Arbiter = arbiter;
            _ownsArbiter = false;
        }
        else
        {
            Arbiter = new DesktopArbiter();
            _ownsArbiter = true;
        }

        if (control != null)
        {
            Control = control;
            _ownsControl = false;
        }
        else
        {
            Control = EmergencyControl.Process;
            _ownsControl = false;
        }
    }

    public static string GetDefaultPipeName()
    {
        var user = Environment.UserName;
        var safe = string.Concat(user.Split(Path.GetInvalidFileNameChars())).Replace(" ", "_");
        return $"inbrisk-runtime-{safe}";
    }

    public static string GetDefaultMutexName()
    {
        var user = Environment.UserName;
        var safe = string.Concat(user.Split(Path.GetInvalidFileNameChars())).Replace(" ", "_");
        return $@"Global\InbriskRuntimeMutex-{safe}";
    }

    public static PipeSecurity CreateDefaultPipeSecurity()
    {
        var ps = new PipeSecurity();
        try
        {
            var currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser != null)
            {
                ps.AddAccessRule(new PipeAccessRule(currentUser, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
            }
            var adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            ps.AddAccessRule(new PipeAccessRule(adminSid, PipeAccessRights.FullControl, AccessControlType.Allow));
            var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            ps.AddAccessRule(new PipeAccessRule(systemSid, PipeAccessRights.FullControl, AccessControlType.Allow));
        }
        catch { }
        return ps;
    }

    public static async Task<bool> CanConnectToPipeAsync(string pipeName, TimeSpan timeout, CancellationToken ct = default)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            await client.ConnectAsync(linked.Token).ConfigureAwait(false);
            return client.IsConnected;
        }
        catch
        {
            return false;
        }
    }

    public static async Task WaitForPipeAvailableAsync(string pipeName, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (await CanConnectToPipeAsync(pipeName, TimeSpan.FromMilliseconds(50), ct).ConfigureAwait(false))
                return;
            await Task.Delay(50, ct).ConfigureAwait(false);
        }
    }

    public static async Task<InbriskRuntimeDaemon?> GetOrCreateDaemonAsync(
        string? pipeName = null,
        string? mutexName = null,
        Func<InbriskRuntimeDaemon>? factory = null,
        Action<InbriskRuntimeDaemon>? onCreated = null,
        CancellationToken ct = default)
    {
        pipeName ??= GetDefaultPipeName();
        mutexName ??= GetDefaultMutexName();

        if (await CanConnectToPipeAsync(pipeName, TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false))
        {
            return null; // Daemon is already running
        }

        Mutex? mutex = null;
        bool acquired = false;
        try
        {
            try
            {
                mutex = new Mutex(true, mutexName, out bool createdNew);
                acquired = createdNew;
            }
            catch (UnauthorizedAccessException)
            {
                // Fallback to Local prefix if Global mutex is restricted in non-elevated desktop session
                var localName = mutexName.Replace(@"Global\", @"Local\");
                mutex = new Mutex(true, localName, out bool createdNew);
                acquired = createdNew;
            }

            if (!acquired)
            {
                try
                {
                    acquired = mutex.WaitOne(TimeSpan.FromSeconds(5));
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }
            }

            if (acquired)
            {
                if (await CanConnectToPipeAsync(pipeName, TimeSpan.FromMilliseconds(50), ct).ConfigureAwait(false))
                {
                    return null;
                }

                var daemon = factory != null ? factory() : new InbriskRuntimeDaemon(pipeName, mutexName);
                await daemon.StartAsync(ct).ConfigureAwait(false);
                onCreated?.Invoke(daemon);
                return daemon;
            }
            else
            {
                await WaitForPipeAvailableAsync(pipeName, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                return null;
            }
        }
        finally
        {
            // Do not release mutex while daemon is running; it protects the daemon instance
        }
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (IsDisposed) throw new ObjectDisposedException(nameof(InbriskRuntimeDaemon));
        if (IsRunning) return;

        if (Control.State == ComputerControlState.EmergencyStopped)
        {
            var userSettings = Inbrisk.Core.UserSettings.Load();
            Control.StartHotkeys(
                Environment.GetEnvironmentVariable("INBRISK_PANIC_HOTKEY") ?? userSettings.PanicHotkey,
                Environment.GetEnvironmentVariable("INBRISK_RESUME_HOTKEY") ?? userSettings.ResumeHotkey);
        }

        IsRunning = true;
        // Pay one-time process-wide activation costs (WinRT projections for
        // OCR/WGC, GDI+ init) off the critical path — otherwise the first
        // session's handshake serializes them.
        _ = Task.Run(WarmProcessWideServices);
        _listenerTask = Task.Run(() => ListenLoopAsync(_cts.Token), _cts.Token);
        using var reg = ct.Register(() => _startedTcs.TrySetCanceled(ct));
        await _startedTcs.Task.ConfigureAwait(false);
    }

    /// <summary>Background first-touch of the process-wide platform services a
    /// session will need (OCR engine activation, WGC capability probe, GDI+
    /// init). Read-only — captures no pixels, injects no input; every failure
    /// is swallowed since warm-up is best-effort.</summary>
    private static void WarmProcessWideServices()
    {
        try { _ = new Inbrisk.Platform.Windows.Ocr.OcrService().Available; }
        catch { }
        try { _ = Windows.Graphics.Capture.GraphicsCaptureSession.IsSupported(); }
        catch { }
        try
        {
            using var bmp = new System.Drawing.Bitmap(2, 2);
            using var g = System.Drawing.Graphics.FromImage(bmp);
            g.Clear(System.Drawing.Color.Transparent);
        }
        catch { }
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        // Boundary: the listener is a fire-and-forget task — a fault escaping
        // the per-iteration catches (e.g. a stream Dispose throwing inside a
        // catch block) must surface as a logged exit, not a faulted task.
        try
        {
            await ListenLoopCoreAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _startedTcs.TrySetException(e);
            System.Diagnostics.Debug.WriteLine($"inbrisk-daemon listen loop exited: {e}");
        }
    }

    private async Task ListenLoopCoreAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !IsDisposed)
        {
            NamedPipeServerStream? serverStream = null;
            try
            {
                serverStream = OperatingSystem.IsWindows()
                    ? NamedPipeServerStreamAcl.Create(
                        PipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous,
                        inBufferSize: 0,
                        outBufferSize: 0,
                        CreateDefaultPipeSecurity())
                    : new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                _startedTcs.TrySetResult();

                await serverStream.WaitForConnectionAsync(ct).ConfigureAwait(false);

                var stream = serverStream;
                _ = Task.Run(() => HandleClientConnectionAsync(stream, ct), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _startedTcs.TrySetCanceled();
                try { serverStream?.Dispose(); } catch { }
                break;
            }
            catch (Exception ex)
            {
                _startedTcs.TrySetException(ex);
                try { serverStream?.Dispose(); } catch { }
                if (ct.IsCancellationRequested || IsDisposed) break;
                try { await Task.Delay(50, ct).ConfigureAwait(false); } catch { }
            }
        }
    }

    private async Task HandleClientConnectionAsync(NamedPipeServerStream stream, CancellationToken ct)
    {
        McpSession? session = null;
        string? sessionId = null;

        try
        {
            // 1. Handshake Phase: Read handshake line byte-by-byte
            var handshakeLine = await ReadLineByteByByteAsync(stream, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(handshakeLine)) return;

            var req = JsonSerializer.Deserialize<DaemonHandshakeRequest>(handshakeLine, JsonOptions);
            if (req == null) return;

            // Verify protocol version
            if (req.ProtocolVersion != CurrentProtocolVersion)
            {
                var errResp = new DaemonHandshakeResponse
                {
                    Success = false,
                    ProtocolVersion = CurrentProtocolVersion,
                    Error = $"Protocol version mismatch: client version {req.ProtocolVersion} is incompatible with daemon version {CurrentProtocolVersion}."
                };
                await WriteLineAsync(stream, JsonSerializer.Serialize(errResp, JsonOptions), ct).ConfigureAwait(false);
                return;
            }

            // Version matched! Create isolated session
            session = _sessionFactory != null
                ? _sessionFactory(this)
                : new McpSession(Control, arbiter: Arbiter, startEvents: _startEvents);
            session.Arbiter = Arbiter;
            sessionId = session.SessionId;

            _activeSessions[sessionId] = session;
            _activeStreams[sessionId] = stream;

            // Send handshake success
            var okResp = new DaemonHandshakeResponse
            {
                Success = true,
                ProtocolVersion = CurrentProtocolVersion,
                SessionId = sessionId,
                SessionEpoch = Epoch
            };
            await WriteLineAsync(stream, JsonSerializer.Serialize(okResp, JsonOptions), ct).ConfigureAwait(false);

            // 2. Stream MCP Session Server
            await RunSessionMcpServerAsync(stream, session, ct).ConfigureAwait(false);
        }
        catch
        {
            // Client disconnected or pipe broke
        }
        finally
        {
            if (sessionId != null)
            {
                _activeSessions.TryRemove(sessionId, out _);
                _activeStreams.TryRemove(sessionId, out _);
            }

            if (session != null)
            {
                try { session.Dispose(); } catch { }
            }

            try { stream.Dispose(); } catch { }
        }
    }

    private async Task RunSessionMcpServerAsync(Stream stream, McpSession session, CancellationToken ct)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true
        });

        builder.Services.AddSingleton(Control);
        builder.Services.AddSingleton(session);

        var serverBuilder = builder.Services
            .AddMcpServer(o =>
            {
                o.ServerInfo = new Implementation { Name = "inbrisk", Version = "0.5.0" };
                o.ServerInstructions = McpHost.GetServerInstructions(Control);
            })
            .WithStreamServerTransport(stream, stream)
            .WithToolsFromAssembly()
            .WithResourcesFromAssembly()
            .WithPromptsFromAssembly();

        McpHost.ConfigureFilters(serverBuilder, _toolProfile);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, session.SessionCts.Token);
        var app = builder.Build();
        await app.RunAsync(linkedCts.Token).ConfigureAwait(false);
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

            if (buffer[0] == '\n')
            {
                break;
            }

            if (buffer[0] != '\r')
            {
                ms.WriteByte(buffer[0]);
            }
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static async Task WriteLineAsync(Stream stream, string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text + "\n");
        await stream.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public McpSession? GetSession(string sessionId) =>
        _activeSessions.TryGetValue(sessionId, out var s) ? s : null;

    public IReadOnlyCollection<McpSession> GetActiveSessions() =>
        _activeSessions.Values.ToList().AsReadOnly();

    public Task StopAsync()
    {
        if (!IsRunning) return Task.CompletedTask;
        IsRunning = false;
        try { _cts.Cancel(); } catch { }

        foreach (var kvp in _activeSessions)
        {
            try { kvp.Value.Dispose(); } catch { }
        }
        _activeSessions.Clear();

        foreach (var kvp in _activeStreams)
        {
            try { kvp.Value.Dispose(); } catch { }
        }
        _activeStreams.Clear();

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        IsRunning = false;

        try { _cts.Cancel(); } catch { }
        try { _cts.Dispose(); } catch { }

        foreach (var kvp in _activeSessions)
        {
            try { kvp.Value.Dispose(); } catch { }
        }
        _activeSessions.Clear();

        foreach (var kvp in _activeStreams)
        {
            try { kvp.Value.Dispose(); } catch { }
        }
        _activeStreams.Clear();

        if (_ownsArbiter)
        {
            try { Arbiter.Dispose(); } catch { }
        }

        if (_ownsControl)
        {
            try { Control.Dispose(); } catch { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        Dispose();
    }
}
