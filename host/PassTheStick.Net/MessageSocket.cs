using System.Net.WebSockets;

namespace PassTheStick.Net;

public sealed record SocketMessage(bool IsText, byte[] Data);

/// <summary>A WebSocket reduced to whole messages, so callers never see fragments and tests can fake it.</summary>
public interface IMessageSocket : IAsyncDisposable
{
    Task ConnectAsync(Uri uri, CancellationToken cancellationToken);

    Task SendTextAsync(string text, CancellationToken cancellationToken);

    Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>Next whole message, or null once the socket is closed.</summary>
    Task<SocketMessage?> ReceiveAsync(CancellationToken cancellationToken);
}

public interface IMessageSocketFactory
{
    IMessageSocket Create();
}

public sealed class WebSocketFactory : IMessageSocketFactory
{
    public IMessageSocket Create() => new WebSocketMessageSocket();
}

/// <summary>
/// ClientWebSocket allows one send at a time, so sends are serialized here, and receives reassemble
/// fragments up to <see cref="MaxMessageBytes"/>. A larger message closes the socket.
/// </summary>
public sealed class WebSocketMessageSocket : IMessageSocket
{
    public const int MaxMessageBytes = 64 * 1024;

    private readonly ClientWebSocket _ws = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public async Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        await _ws.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
    }

    public Task SendTextAsync(string text, CancellationToken cancellationToken) =>
        SendAsync(System.Text.Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, cancellationToken);

    public Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
        SendAsync(data, WebSocketMessageType.Binary, cancellationToken);

    public async Task<SocketMessage?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var assembled = new MemoryStream();
        while (_ws.State == WebSocketState.Open)
        {
            var result = await _ws.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (assembled.Length + result.Count > MaxMessageBytes)
            {
                await _ws.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, "too large", CancellationToken.None).ConfigureAwait(false);
                return null;
            }

            assembled.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return new SocketMessage(result.MessageType == WebSocketMessageType.Text, assembled.ToArray());
            }
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_ws.State == WebSocketState.Open)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
            {
                // The peer is already gone; disposing below is enough.
            }
        }

        _ws.Dispose();
        _sendLock.Dispose();
    }

    private async Task SendAsync(ReadOnlyMemory<byte> data, WebSocketMessageType type, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _ws.SendAsync(data, type, true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }
}
