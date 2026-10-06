using System.Net.WebSockets;
using System.Text;

namespace Inbrisk.Runtime.Adapters;

/// <summary>
/// Authoritative duplex transport abstraction for Chrome DevTools Protocol communication.
/// </summary>
public interface ICdpTransport : IAsyncDisposable
{
    bool IsOpen { get; }
    Task SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct);
    Task<string?> ReceiveMessageAsync(CancellationToken ct);
    Task CloseAsync(CancellationToken ct);
}

/// <summary>
/// Production WebSocket-backed CDP transport wrapping <see cref="ClientWebSocket"/>.
/// </summary>
public sealed class WebSocketCdpTransport : ICdpTransport
{
    private readonly ClientWebSocket _ws;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private int _disposed;

    public bool IsOpen => _disposed == 0 && _ws.State == WebSocketState.Open;

    public WebSocketCdpTransport(ClientWebSocket ws)
    {
        _ws = ws;
    }

    public static async Task<WebSocketCdpTransport> ConnectAsync(Uri uri, TimeSpan timeout, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(timeout);
        await ws.ConnectAsync(uri, connectCts.Token).ConfigureAwait(false);
        return new WebSocketCdpTransport(ws);
    }

    public async Task SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _ws.SendAsync(message, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task<string?> ReceiveMessageAsync(CancellationToken ct)
    {
        if (_disposed != 0 || _ws.State != WebSocketState.Open)
            return null;

        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();

        while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            WebSocketReceiveResult res;
            try
            {
                res = await _ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (WebSocketException)
            {
                return null;
            }

            if (res.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            ms.Write(buffer, 0, res.Count);
            if (res.EndOfMessage)
            {
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        return null;
    }

    public async Task CloseAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            if (_ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var closeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                closeCts.CancelAfter(TimeSpan.FromSeconds(2));
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", closeCts.Token).ConfigureAwait(false);
            }
        }
        catch { }
        finally
        {
            _ws.Dispose();
            _sendLock.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
