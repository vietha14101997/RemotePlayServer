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

// Reconnect state machine (backoff loop, single-flight guard, token cooperation) lives in
// RelayClient.Reconnect.cs — split by concern, see that file's header comment.
public partial class RelayClient : IDisposable
{
    private readonly HttpClient _httpClient = new();
    private ClientWebSocket? _presenceWs;
    private CancellationTokenSource? _cts;

    private string? _relayUrl;
    private string? _accessToken;
    private string? _refreshToken;
    private string? _deviceId;

    // H1: serializes token refreshes. The relay rotates+revokes the refresh token on every
    // /auth/refresh, so the 14-min TokenRefreshLoop racing a reconnect's EnsureFreshTokenAsync
    // must not both refresh with the same _refreshToken (the loser would 401 on a revoked token).
    private readonly SemaphoreSlim _tokenRefreshLock = new(1, 1);
    // Serializes all WS sends: ClientWebSocket.SendAsync forbids concurrent calls,
    // and relay-media fires frames from encoder threads alongside signaling sends.
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private bool _disposed;

    public event Action<string>? OnSessionRequest;
    public event Action<string>? OnTextMessage;
    public event Action<byte[]>? OnBinaryMessage;
    public event Action? OnRoomReady;
    public event Action? OnPeerDisconnected;
    public event Action<bool>? OnConnectionStateChanged;

    public bool IsConnected => _presenceWs?.State == WebSocketState.Open;
    public string? DeviceId => _deviceId;
    public string? LastAuthError { get; private set; }

    /// <summary>Base HTTP(S) URL of the relay this client is registered/connected to (e.g. "https://relay.example.com"). Null until Login/RegisterAsync sets it.</summary>
    public string? RelayUrl => _relayUrl;

    public List<IceServerConfig>? IceServers { get; private set; }

    public async Task<(bool Success, string? Error)> RegisterAsync(string relayUrl, string email, string username, string password)
    {
        LastAuthError = null;

        if (string.IsNullOrWhiteSpace(relayUrl))
            return (false, "Relay URL is required");

        _relayUrl = relayUrl.TrimEnd('/');

        var body = JsonSerializer.Serialize(new { email, username, password });
        using var content = new StringContent(body, TextEncoding.UTF8, new System.Net.Http.Headers.MediaTypeHeaderValue("application/json"));

        try
        {
            using var resp = await _httpClient.PostAsync($"{_relayUrl}/auth/register", content);
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

            Logger.Info("[Relay] Registration successful — signing in");
            var loggedIn = await LoginAsync(_relayUrl, email, password);
            return (loggedIn, loggedIn ? null : $"Account created, but sign-in failed: {LastAuthError ?? "unknown error"}");
        }
        catch (Exception ex)
        {
            Logger.Error($"[Relay] Register error: {ex.Message}");
            return (false, ex.Message);
        }
    }

    public async Task<bool> LoginAsync(string relayUrl, string email, string password)
    {
        LastAuthError = null;

        if (string.IsNullOrWhiteSpace(relayUrl))
        {
            LastAuthError = "Relay URL is required";
            return false;
        }

        _relayUrl = relayUrl.TrimEnd('/');

        var body = JsonSerializer.Serialize(new { email, password, device_name = Environment.MachineName });
        using var content = new StringContent(body, TextEncoding.UTF8, new System.Net.Http.Headers.MediaTypeHeaderValue("application/json"));

        try
        {
            using var resp = await _httpClient.PostAsync($"{_relayUrl}/auth/login", content);
            if (!resp.IsSuccessStatusCode)
            {
                var error = await resp.Content.ReadAsStringAsync();
                LastAuthError = FormatAuthError(resp.StatusCode, error);
                Logger.Error($"[Relay] Login failed: {resp.StatusCode} - {error}");
                return false;
            }

            var json = await resp.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<LoginResponse>(json);
            ApplyAuthTokens(result);

            if (_accessToken == null)
            {
                LastAuthError = "Relay returned an invalid login response without an access token.";
                Logger.Error("[Relay] Login failed: invalid success response without an access token");
                return false;
            }

            Logger.Info("[Relay] Login successful");
            return true;
        }
        catch (Exception ex)
        {
            LastAuthError = $"Relay connection error: {ex.Message}";
            Logger.Error($"[Relay] Login error: {ex.Message}");
            return false;
        }
    }

    internal static string FormatAuthError(System.Net.HttpStatusCode statusCode, string responseBody)
    {
        if (statusCode == System.Net.HttpStatusCode.NotFound)
            return "Relay authentication endpoint was not found. Check the Relay URL and deployed server version.";

        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(responseBody);
            if (json.TryGetProperty("error", out var error))
                return error.GetString() ?? $"Relay login failed ({(int)statusCode})";
            if (json.TryGetProperty("message", out var message))
                return message.GetString() ?? $"Relay login failed ({(int)statusCode})";
        }
        catch (JsonException) { }

        return $"Relay login failed ({(int)statusCode})";
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

    /// <summary>
    /// Opens the presence WebSocket (device/session identity is read from stored instance
    /// fields, so this is also the reconnect entry point — see RelayClient.Reconnect.cs).
    /// Reusable: tears down any previous connection/loops before establishing a new one so
    /// repeated calls (reconnect attempts) don't leak read/token-refresh loops.
    /// </summary>
    public async Task ConnectPresenceAsync()
    {
        if (_accessToken == null || _relayUrl == null || _deviceId == null) return;

        // Tear down the previous attempt's loops/socket before starting a new one —
        // without this, a reconnect would orphan the old ReadLoopAsync/TokenRefreshLoopAsync
        // (they'd keep running on the stale CancellationTokenSource forever). Cancel() only
        // (no Dispose()): SendTextAsync/SendBinaryAsync may concurrently read _cts.Token, which
        // throws ObjectDisposedException post-Dispose but is always safe after a plain Cancel().
        _cts?.Cancel();
        _presenceWs?.Dispose();

        // Preserve the Reconnecting state set by the backoff loop across each attempt;
        // only show "Connecting" for a fresh (non-reconnect) connection.
        if (State != RelayConnectionState.Reconnecting)
            SetState(RelayConnectionState.Connecting);

        _cts = new CancellationTokenSource();
        _presenceWs = new ClientWebSocket();

        var wsUrl = _relayUrl.Replace("https://", "wss://").Replace("http://", "ws://");
        var uri = new Uri($"{wsUrl}/ws/server?token={_accessToken}&device_id={_deviceId}");

        try
        {
            await _presenceWs.ConnectAsync(uri, _cts.Token);
            Logger.Info("[Relay] Presence WebSocket connected");
            OnConnectionStateChanged?.Invoke(true);
            SetState(RelayConnectionState.Connected);

            _ = ReadLoopAsync(_cts.Token);
            _ = TokenRefreshLoopAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            Logger.Error($"[Relay] Presence connect error: {ex.Message}");
            OnConnectionStateChanged?.Invoke(false);

            // Only clear back to Disconnected outside a reconnect attempt — inside the backoff
            // loop, State must stay Reconnecting so the loop keeps retrying (and the UI keeps
            // showing "Reconnecting…" rather than flashing "Disconnected" between attempts).
            if (State != RelayConnectionState.Reconnecting)
                SetState(RelayConnectionState.Disconnected);
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

            if (_userDisconnectRequested)
            {
                // Graceful/user-initiated teardown (Dispose()) — do not reconnect.
                SetState(RelayConnectionState.Disconnected);
            }
            else
            {
                // Transport drop (network blip, relay restart, etc.) — auto-reconnect.
                SetState(RelayConnectionState.Reconnecting);
                _ = TryStartReconnectLoopAsync();
            }
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
        var token = _cts?.Token ?? CancellationToken.None;
        await _sendLock.WaitAsync(token);
        try
        {
            await _presenceWs.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                true,
                token);
        }
        catch (Exception ex)
        {
            Logger.Error($"[Relay] Send text error: {ex.Message}");
        }
        finally { _sendLock.Release(); }
    }

    public async Task SendBinaryAsync(byte[] data)
    {
        if (_presenceWs?.State != WebSocketState.Open) return;

        var token = _cts?.Token ?? CancellationToken.None;
        await _sendLock.WaitAsync(token);
        try
        {
            await _presenceWs.SendAsync(
                new ArraySegment<byte>(data),
                WebSocketMessageType.Binary,
                true,
                token);
        }
        catch (Exception ex)
        {
            Logger.Error($"[Relay] Send binary error: {ex.Message}");
        }
        finally { _sendLock.Release(); }
    }

    private async Task TokenRefreshLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(14), ct); // Refresh 1 min before 15 min expiry
                await RefreshTokenAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on reconnect/disconnect teardown (ConnectPresenceAsync/Dispose cancel _cts).
        }
    }

    private async Task RefreshTokenAsync()
    {
        // H1: single-flight. Concurrent callers serialize here; each reads the CURRENT
        // _refreshToken inside the lock, so a caller that waits picks up the token the
        // prior refresh just rotated in (instead of replaying a now-revoked one).
        await _tokenRefreshLock.WaitAsync();
        try
        {
            if (_refreshToken == null || _relayUrl == null) return;

            var body = JsonSerializer.Serialize(new { refresh_token = _refreshToken });
            var content = new StringContent(body, TextEncoding.UTF8, new System.Net.Http.Headers.MediaTypeHeaderValue("application/json"));

            var resp = await _httpClient.PostAsync($"{_relayUrl}/auth/refresh", content);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<LoginResponse>(json);
                ApplyAuthTokens(result);
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
        finally
        {
            _tokenRefreshLock.Release();
        }
    }

    /// <summary>
    /// Stores tokens from a login/register/refresh response and tracks the access token's
    /// expiry so reconnect logic (RelayClient.Reconnect.cs) can refresh proactively instead
    /// of retrying the presence WS with a stale JWT.
    /// </summary>
    private void ApplyAuthTokens(LoginResponse? result)
    {
        _accessToken = result?.AccessToken;
        _refreshToken = result?.RefreshToken;
        _tokenExpiresAt = result != null && result.ExpiresIn > 0
            ? DateTimeOffset.UtcNow.AddSeconds(result.ExpiresIn)
            : null;
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

        // Mark as a graceful/user-initiated disconnect BEFORE cancelling — ReadLoopAsync's
        // finally block checks this flag to decide whether to auto-reconnect (it must not).
        // Cancel() only (no Dispose()) here: other in-flight code may still read _cts.Token /
        // _reconnectLoopCts concurrently, and CancellationTokenSource.Token throws
        // ObjectDisposedException post-Dispose but Cancel() is always safe to call.
        _userDisconnectRequested = true;
        _reconnectLoopCts?.Cancel();

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
