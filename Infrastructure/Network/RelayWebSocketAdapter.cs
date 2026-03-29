#nullable enable
using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;

namespace RemotePlayServer.Infrastructure.Network;

/// <summary>
/// Adapts RelayClient's presence WebSocket into a WebSocket that PhaseProtocolHandler can use.
/// Messages received from relay are queued, and sends go through the relay client.
/// </summary>
public class RelayWebSocketAdapter : WebSocket
{
    private readonly RelayClient _relayClient;
    private readonly BlockingCollection<(WebSocketMessageType type, byte[] data)> _incomingMessages = new();
    private WebSocketState _state = WebSocketState.Open;
    private WebSocketCloseStatus? _closeStatus;
    private string? _closeDescription;

    public RelayWebSocketAdapter(RelayClient relayClient)
    {
        _relayClient = relayClient;

        // Subscribe to relay messages and queue them
        relayClient.OnTextMessage += text =>
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            _incomingMessages.TryAdd((WebSocketMessageType.Text, bytes));
        };

        relayClient.OnBinaryMessage += data =>
        {
            _incomingMessages.TryAdd((WebSocketMessageType.Binary, data));
        };

        relayClient.OnPeerDisconnected += () =>
        {
            _state = WebSocketState.CloseReceived;
            _closeStatus = WebSocketCloseStatus.NormalClosure;
            _closeDescription = "peer_disconnected";
            _incomingMessages.TryAdd((WebSocketMessageType.Close, Array.Empty<byte>()));
        };
    }

    public override WebSocketState State => _state;
    public override WebSocketCloseStatus? CloseStatus => _closeStatus;
    public override string? CloseStatusDescription => _closeDescription;
    public override string? SubProtocol => null;

    public override async Task<WebSocketReceiveResult> ReceiveAsync(
        ArraySegment<byte> buffer, CancellationToken ct)
    {
        // Block until a message arrives from relay
        var (type, data) = await Task.Run(() =>
        {
            _incomingMessages.TryTake(out var msg, Timeout.Infinite, ct);
            return msg;
        }, ct);

        if (type == WebSocketMessageType.Close)
        {
            _state = WebSocketState.CloseReceived;
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true,
                WebSocketCloseStatus.NormalClosure, "peer_disconnected");
        }

        var count = Math.Min(data.Length, buffer.Count);
        Buffer.BlockCopy(data, 0, buffer.Array!, buffer.Offset, count);
        return new WebSocketReceiveResult(count, type, true);
    }

    public override async Task SendAsync(
        ArraySegment<byte> buffer, WebSocketMessageType messageType,
        bool endOfMessage, CancellationToken ct)
    {
        if (messageType == WebSocketMessageType.Text)
        {
            var text = System.Text.Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count);
            await _relayClient.SendTextAsync(text);
        }
        else if (messageType == WebSocketMessageType.Binary)
        {
            var data = new byte[buffer.Count];
            Buffer.BlockCopy(buffer.Array!, buffer.Offset, data, 0, buffer.Count);
            await _relayClient.SendBinaryAsync(data);
        }
    }

    public override async Task CloseAsync(
        WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken ct)
    {
        _state = WebSocketState.Closed;
        _closeStatus = closeStatus;
        _closeDescription = statusDescription;
        await Task.CompletedTask;
    }

    public override async Task CloseOutputAsync(
        WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken ct)
    {
        _state = WebSocketState.CloseSent;
        await Task.CompletedTask;
    }

    public override void Abort()
    {
        _state = WebSocketState.Aborted;
        _incomingMessages.CompleteAdding();
    }

    public override void Dispose()
    {
        _incomingMessages.Dispose();
    }
}
