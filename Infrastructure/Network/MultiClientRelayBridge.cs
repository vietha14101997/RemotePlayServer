#nullable enable
using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RemotePlayServer.Core;

using TextEncoding = System.Text.Encoding;

namespace RemotePlayServer.Infrastructure.Network;

/// <summary>
/// Routes relay messages to per-client WebSocket adapters based on _client_id field.
/// Each client gets its own PerClientAdapter (WebSocket) for PhaseProtocolHandler.
/// Server sends are tagged with "target" for relay to route to specific client.
/// </summary>
public class MultiClientRelayBridge : IDisposable
{
    private readonly RelayClient _relayClient;
    private readonly ConcurrentDictionary<string, PerClientAdapter> _adapters = new();
    private bool _disposed;

    public MultiClientRelayBridge(RelayClient relayClient)
    {
        _relayClient = relayClient;

        relayClient.OnTextMessage += OnRelayText;
        relayClient.OnBinaryMessage += OnRelayBinary;
        relayClient.OnPeerDisconnected += OnRelayPeerDisconnected;
    }

    /// <summary>
    /// Create a per-client adapter. PhaseProtocolHandler uses this as its WebSocket.
    /// </summary>
    public PerClientAdapter CreateAdapter(string clientId)
    {
        var adapter = new PerClientAdapter(clientId, _relayClient);
        _adapters[clientId] = adapter;
        Logger.Info($"[RelayBridge] Adapter created for client {clientId}");
        return adapter;
    }

    public void RemoveAdapter(string clientId)
    {
        if (_adapters.TryRemove(clientId, out var adapter))
        {
            adapter.SignalClose("client removed");
            Logger.Info($"[RelayBridge] Adapter removed for client {clientId}");
        }
    }

    public int ActiveCount => _adapters.Count;

    private void OnRelayText(string text)
    {
        // Try to extract _client_id from JSON to route to specific adapter
        string? clientId = null;
        try
        {
            if (text.Length > 0 && text[0] == '{')
            {
                var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("_client_id", out var cid))
                    clientId = cid.GetString();
            }
        }
        catch { }

        var bytes = TextEncoding.UTF8.GetBytes(text);

        if (clientId != null && _adapters.TryGetValue(clientId, out var adapter))
        {
            // Targeted message → route to specific client adapter
            adapter.EnqueueMessage(WebSocketMessageType.Text, bytes);
        }
        else
        {
            // Broadcast to all adapters (or non-JSON like ping/pong)
            foreach (var a in _adapters.Values)
                a.EnqueueMessage(WebSocketMessageType.Text, bytes);
        }
    }

    private void OnRelayBinary(byte[] data)
    {
        // Binary → broadcast to all adapters
        foreach (var a in _adapters.Values)
            a.EnqueueMessage(WebSocketMessageType.Binary, data);
    }

    private void OnRelayPeerDisconnected()
    {
        // Notify all adapters
        foreach (var a in _adapters.Values)
            a.SignalClose("peer_disconnected");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _relayClient.OnTextMessage -= OnRelayText;
        _relayClient.OnBinaryMessage -= OnRelayBinary;
        _relayClient.OnPeerDisconnected -= OnRelayPeerDisconnected;

        foreach (var a in _adapters.Values)
            a.SignalClose("bridge disposed");
        _adapters.Clear();
    }
}

/// <summary>
/// Per-client WebSocket adapter. Routes sends through relay with "target" field.
/// </summary>
public class PerClientAdapter : WebSocket
{
    private readonly string _clientId;
    private readonly RelayClient _relayClient;
    private readonly BlockingCollection<(WebSocketMessageType type, byte[] data)> _inbox = new();
    private WebSocketState _state = WebSocketState.Open;
    private WebSocketCloseStatus? _closeStatus;
    private string? _closeDescription;

    public string ClientId => _clientId;

    public PerClientAdapter(string clientId, RelayClient relayClient)
    {
        _clientId = clientId;
        _relayClient = relayClient;
    }

    public void EnqueueMessage(WebSocketMessageType type, byte[] data)
    {
        if (_state == WebSocketState.Open)
            _inbox.TryAdd((type, data));
    }

    public void SignalClose(string reason)
    {
        _state = WebSocketState.CloseReceived;
        _closeStatus = WebSocketCloseStatus.NormalClosure;
        _closeDescription = reason;
        _inbox.TryAdd((WebSocketMessageType.Close, Array.Empty<byte>()));
    }

    public override WebSocketState State => _state;
    public override WebSocketCloseStatus? CloseStatus => _closeStatus;
    public override string? CloseStatusDescription => _closeDescription;
    public override string? SubProtocol => null;

    public override async Task<WebSocketReceiveResult> ReceiveAsync(
        ArraySegment<byte> buffer, CancellationToken ct)
    {
        var (type, data) = await Task.Run(() =>
        {
            _inbox.TryTake(out var msg, Timeout.Infinite, ct);
            return msg;
        }, ct);

        if (type == WebSocketMessageType.Close)
        {
            _state = WebSocketState.CloseReceived;
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true,
                WebSocketCloseStatus.NormalClosure, _closeDescription ?? "closed");
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
            var text = TextEncoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count);
            // Send as-is — relay broadcasts to all clients in room.
            // Target-based routing not needed: each room client receives all messages.
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
        _inbox.CompleteAdding();
    }

    public override void Dispose()
    {
        _inbox.Dispose();
    }
}
