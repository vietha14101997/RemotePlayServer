#nullable enable
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using RemotePlayServer.Core;

using TextEncoding = System.Text.Encoding;

namespace RemotePlayServer.Infrastructure.Network;

public class RelayClient : IDisposable
{
    private readonly HttpClient _httpClient = new();
    private ClientWebSocket? _presenceWs;
    private CancellationTokenSource? _cts;

    private string? _relayUrl;
    private string? _accessToken;
    private string? _refreshToken;
    private string? _deviceId;
    private bool _disposed;

    public event Action<string>? OnSessionRequest;
    public event Action<string>? OnTextMessage;
    public event Action<byte[]>? OnBinaryMessage;
    public event Action? OnRoomReady;
    public event Action? OnPeerDisconnected;
    public event Action<bool>? OnConnectionStateChanged;

    public bool IsConnected => _presenceWs?.State == WebSocketState.Open;
    public string? DeviceId => _deviceId;

    public List<IceServerConfig>? IceServers { get; private set; }

    public async Task<(bool Success, string? Error)> RegisterAsync(string relayUrl, string email, string username, string password)
    {
        _relayUrl = relayUrl.TrimEnd('/');

        var body = JsonSerializer.Serialize(new { email, username, password });
        var content = new StringContent(body, TextEncoding.UTF8, new System.Net.Http.Headers.MediaTypeHeaderValue("application/json"));

        try
        {
            var resp = await _httpClient.PostAsync($"{_relayUrl}/auth/register", content);
            var json = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
            {
                var errorMsg = "Registration failed";
                try
                {
                    var errObj = JsonSerializer.Deserialize<JsonElement>(json);
                    if (errObj.TryGetProperty("error", out var errProp))
                        errorMsg = errProp.GetString() ?? errorMsg;
                }
                catch { }
                Logger.Error($"[Relay] Register failed: {resp.StatusCode} - {errorMsg}");
                return (false, errorMsg);
            }

            var result = JsonSerializer.Deserialize<LoginResponse>(json);
            _accessToken = result?.AccessToken;
            _refreshToken = result?.RefreshToken;

            Logger.Info("[Relay] Registration successful");
            return (_accessToken != null, null);
        }
        catch (Exception ex)
        {
            Logger.Error($"[Relay] Register error: {ex.Message}");
            return (false, ex.Message);
        }
    }

    public async Task<bool> LoginAsync(string relayUrl, string email, string password)
    {
        _relayUrl = relayUrl.TrimEnd('/');

        var body = JsonSerializer.Serialize(new { email, password, device_name = Environment.MachineName });
        var content = new StringContent(body, TextEncoding.UTF8, new System.Net.Http.Headers.MediaTypeHeaderValue("application/json"));

        try
        {
            var resp = await _httpClient.PostAsync($"{_relayUrl}/auth/login", content);
            if (!resp.IsSuccessStatusCode)
            {
                Logger.Error($"[Relay] Login failed: {resp.StatusCode}");
                return false;
            }

            var json = await resp.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<LoginResponse>(json);
            _accessToken = result?.AccessToken;
            _refreshToken = result?.RefreshToken;

            Logger.Info("[Relay] Login successful");
            return _accessToken != null;
        }
        catch (Exception ex)
        {
            Logger.Error($"[Relay] Login error: {ex.Message}");
            return false;
        }
    }

    public async Task<string?> RegisterDeviceAsync(string deviceName, object? hwInfo = null)
    {
        if (_accessToken == null || _relayUrl == null) return null;

        var body = JsonSerializer.Serialize(new { device_name = deviceName, device_type = "windows_server", hw_info = hwInfo });
        var content = new StringContent(body, TextEncoding.UTF8, new System.Net.Http.Headers.MediaTypeHeaderValue("application/json"));

        var request = new HttpRequestMessage(HttpMethod.Post, $"{_relayUrl}/devices/register");
        request.Content = content;
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);

        try
        {
            var resp = await _httpClient.SendAsync(request);
            var json = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
            {
                Logger.Error($"[Relay] Device registration failed: {resp.StatusCode} - {json}");
                return null;
            }

            var result = JsonSerializer.Deserialize<JsonElement>(json);
            _deviceId = result.GetProperty("device_id").GetString();
            Logger.Info($"[Relay] Device registered: {_deviceId}");
            return _deviceId;
        }
        catch (Exception ex)
        {
            Logger.Error($"[Relay] Device registration error: {ex.Message}");
            return null;
        }
    }

    public async Task<List<IceServerConfig>?> FetchIceServersAsync()
    {
        if (_accessToken == null || _relayUrl == null) return null;

        var request = new HttpRequestMessage(HttpMethod.Get, $"{_relayUrl}/ice-servers");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);

        try
        {
            var resp = await _httpClient.SendAsync(request);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<IceServersResponse>(json);
            IceServers = result?.IceServers;
            Logger.Info($"[Relay] Fetched {IceServers?.Count ?? 0} ICE servers");
            return IceServers;
        }
        catch (Exception ex)
        {
            Logger.Error($"[Relay] Fetch ICE servers error: {ex.Message}");
            return null;
        }
    }

    public async Task ConnectPresenceAsync()
    {
        if (_accessToken == null || _relayUrl == null || _deviceId == null) return;

        _cts = new CancellationTokenSource();
        _presenceWs = new ClientWebSocket();

        var wsUrl = _relayUrl.Replace("https://", "wss://").Replace("http://", "ws://");
        var uri = new Uri($"{wsUrl}/ws/server?token={_accessToken}&device_id={_deviceId}");

        try
        {
            await _presenceWs.ConnectAsync(uri, _cts.Token);
            Logger.Info("[Relay] Presence WebSocket connected");
            OnConnectionStateChanged?.Invoke(true);

            _ = ReadLoopAsync(_cts.Token);
            _ = TokenRefreshLoopAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            Logger.Error($"[Relay] Presence connect error: {ex.Message}");
            OnConnectionStateChanged?.Invoke(false);
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[1024 * 64]; // 64KB

        try
        {
            while (_presenceWs?.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await _presenceWs.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Logger.Info("[Relay] Presence WS closed by server");
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var text = TextEncoding.UTF8.GetString(buffer, 0, result.Count);
                    HandleTextMessage(text);
                }
                else if (result.MessageType == WebSocketMessageType.Binary)
                {
                    var data = new byte[result.Count];
                    Buffer.BlockCopy(buffer, 0, data, 0, result.Count);
                    OnBinaryMessage?.Invoke(data);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.Error($"[Relay] Presence read error: {ex.Message}");
        }
        finally
        {
            OnConnectionStateChanged?.Invoke(false);
            Logger.Info("[Relay] Presence WS disconnected");
        }
    }

    private void HandleTextMessage(string text)
    {
        // Skip non-JSON messages (e.g., "ping:timestamp", "pong:timestamp")
        if (text.Length == 0 || text[0] != '{')
        {
            OnTextMessage?.Invoke(text);
            return;
        }

        try
        {
            var msg = JsonSerializer.Deserialize<JsonElement>(text);
            var type = msg.GetProperty("type").GetString();

            switch (type)
            {
                case "session_request":
                    var sessionId = msg.GetProperty("session_id").GetString();
                    Logger.Info($"[Relay] Session request: {sessionId}");
                    OnSessionRequest?.Invoke(sessionId!);
                    break;

                case "room_ready":
                    // Room ready notification from relay — client WS connected to room.
                    // Only fire handler once (client_joining already handled separately).
                    Logger.Info("[Relay] Room ready (client WS connected)");
                    OnRoomReady?.Invoke();
                    break;

                case "client_joining":
                    // Pre-notification before WS connect — log only, don't create handler.
                    var joinClientId = msg.GetProperty("client_id").GetString();
                    var joinRole = msg.GetProperty("role").GetString();
                    Logger.Info($"[Relay] Client joining: {joinClientId} as {joinRole}");
                    break;

                case "peer_disconnected":
                    Logger.Info("[Relay] Peer disconnected");
                    OnPeerDisconnected?.Invoke();
                    break;

                default:
                    // Forward protocol messages (hardware_info_ack, offer, candidate, etc.)
                    OnTextMessage?.Invoke(text);
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[Relay] Parse error: {ex.Message}");
        }
    }

    public async Task SendTextAsync(string text)
    {
        if (_presenceWs?.State != WebSocketState.Open) return;

        var bytes = TextEncoding.UTF8.GetBytes(text);
        try
        {
            await _presenceWs.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                true,
                _cts?.Token ?? CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logger.Error($"[Relay] Send text error: {ex.Message}");
        }
    }

    public async Task SendBinaryAsync(byte[] data)
    {
        if (_presenceWs?.State != WebSocketState.Open) return;

        try
        {
            await _presenceWs.SendAsync(
                new ArraySegment<byte>(data),
                WebSocketMessageType.Binary,
                true,
                _cts?.Token ?? CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logger.Error($"[Relay] Send binary error: {ex.Message}");
        }
    }

    private async Task TokenRefreshLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(14), ct); // Refresh 1 min before 15 min expiry
            await RefreshTokenAsync();
        }
    }

    private async Task RefreshTokenAsync()
    {
        if (_refreshToken == null || _relayUrl == null) return;

        var body = JsonSerializer.Serialize(new { refresh_token = _refreshToken });
        var content = new StringContent(body, TextEncoding.UTF8, new System.Net.Http.Headers.MediaTypeHeaderValue("application/json"));

        try
        {
            var resp = await _httpClient.PostAsync($"{_relayUrl}/auth/refresh", content);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<LoginResponse>(json);
                _accessToken = result?.AccessToken;
                _refreshToken = result?.RefreshToken;
                Logger.Info("[Relay] Token refreshed");
            }
            else
            {
                Logger.Error($"[Relay] Token refresh failed: {resp.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[Relay] Token refresh error: {ex.Message}");
        }
    }

    public async Task<bool> RegisterGuestDeviceAsync(string shortId, string password, string deviceName)
    {
        if (_accessToken == null || _relayUrl == null || _deviceId == null) return false;

        var body = JsonSerializer.Serialize(new
        {
            short_id = shortId,
            password = password,
            device_name = deviceName,
            device_id = _deviceId
        });
        var content = new StringContent(body, TextEncoding.UTF8,
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/json"));

        var request = new HttpRequestMessage(HttpMethod.Post, $"{_relayUrl}/guest/register");
        request.Content = content;
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);

        try
        {
            var resp = await _httpClient.SendAsync(request);
            if (resp.IsSuccessStatusCode)
            {
                Logger.Info($"[Relay] Guest device registered: {shortId}");
                return true;
            }

            var error = await resp.Content.ReadAsStringAsync();
            Logger.Error($"[Relay] Guest registration failed: {resp.StatusCode} - {error}");
            return false;
        }
        catch (Exception ex)
        {
            Logger.Error($"[Relay] Guest registration error: {ex.Message}");
            return false;
        }
    }

    public async Task<string?> CreateRoomAsync()
    {
        if (_accessToken == null || _relayUrl == null || _deviceId == null) return null;

        var request = new HttpRequestMessage(HttpMethod.Post, $"{_relayUrl}/rooms/create?device_id={_deviceId}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);

        try
        {
            var resp = await _httpClient.SendAsync(request);
            var json = await resp.Content.ReadAsStringAsync();

            if (resp.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                // Room already exists — parse room_id from existing
                Logger.Info("[Relay] Room already exists for this device");
                var existing = JsonSerializer.Deserialize<JsonElement>(json);
                return existing.TryGetProperty("room_id", out var rid) ? rid.GetString() : null;
            }

            if (!resp.IsSuccessStatusCode)
            {
                Logger.Error($"[Relay] Create room failed: {resp.StatusCode} - {json}");
                return null;
            }

            var result = JsonSerializer.Deserialize<JsonElement>(json);
            var roomId = result.GetProperty("room_id").GetString();
            Logger.Info($"[Relay] Room created: {roomId}");
            return roomId;
        }
        catch (Exception ex)
        {
            Logger.Error($"[Relay] Create room error: {ex.Message}");
            return null;
        }
    }

    public async Task<bool> SetRoomPasswordAsync(string roomId, string password)
    {
        if (_accessToken == null || _relayUrl == null) return false;

        var body = JsonSerializer.Serialize(new { room_id = roomId, password });
        var content = new StringContent(body, TextEncoding.UTF8,
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/json"));

        var request = new HttpRequestMessage(HttpMethod.Post, $"{_relayUrl}/rooms/set-password");
        request.Content = content;
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);

        try
        {
            var resp = await _httpClient.SendAsync(request);
            if (resp.IsSuccessStatusCode)
            {
                Logger.Info($"[Relay] Room password set for {roomId}");
                return true;
            }
            Logger.Error($"[Relay] Set room password failed: {resp.StatusCode}");
            return false;
        }
        catch (Exception ex)
        {
            Logger.Error($"[Relay] Set room password error: {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        _presenceWs?.Dispose();
        _httpClient.Dispose();
    }
}

// --- DTOs ---

public class LoginResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }
}

public class IceServerConfig
{
    [JsonPropertyName("urls")]
    public List<string> Urls { get; set; } = new();

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("credential")]
    public string? Credential { get; set; }
}

public class IceServersResponse
{
    [JsonPropertyName("ice_servers")]
    public List<IceServerConfig>? IceServers { get; set; }

    [JsonPropertyName("ttl")]
    public int Ttl { get; set; }
}
