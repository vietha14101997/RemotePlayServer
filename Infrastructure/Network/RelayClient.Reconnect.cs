#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using RemotePlayServer.Core;

namespace RemotePlayServer.Infrastructure.Network;

/// <summary>
/// Reconnect state machine for the presence WebSocket (Phase 2 — Signaling Resilience).
/// Split from RelayClient.cs by concern: distinguishes a graceful/user-initiated
/// disconnect from a transport drop and, on a drop, runs a single-flight exponential
/// backoff loop that re-invokes ConnectPresenceAsync (reusing the stored device/access
/// token) until it succeeds or the user explicitly disconnects.
/// </summary>
public partial class RelayClient
{
    /// <summary>Coarse connection lifecycle, surfaced to the UI/ViewModel layer.</summary>
    public enum RelayConnectionState
    {
        Disconnected,
        Connecting,
        Connected,
        Reconnecting,
    }

    /// <summary>Fires whenever <see cref="State"/> changes; richer complement to OnConnectionStateChanged.</summary>
    public event Action<RelayConnectionState>? OnRelayStateChanged;

    /// <summary>Current reconnect state machine value.</summary>
    public RelayConnectionState State { get; private set; } = RelayConnectionState.Disconnected;

    private readonly RelayReconnectPolicy _reconnectPolicy = new();
    private CancellationTokenSource? _reconnectLoopCts;
    private int _reconnectInFlight; // 0 = idle, 1 = a reconnect loop owns this client (Interlocked guard)
    private volatile bool _userDisconnectRequested;
    private DateTimeOffset? _tokenExpiresAt;

    /// <summary>Refresh the JWT this far ahead of its expiry before attempting a reconnect.</summary>
    private static readonly TimeSpan TokenRefreshBuffer = TimeSpan.FromSeconds(30);

    private void SetState(RelayConnectionState state)
    {
        if (State == state) return;
        State = state;
        OnRelayStateChanged?.Invoke(state);
    }

    /// <summary>
    /// Single-flight backoff loop: reconnects via ConnectPresenceAsync (same device/session
    /// id, already held in instance fields) until it succeeds or the user disconnects.
    /// Safe to call multiple times concurrently — only the first caller runs the loop.
    /// </summary>
    private async Task TryStartReconnectLoopAsync()
    {
        if (Interlocked.Exchange(ref _reconnectInFlight, 1) == 1)
            return; // a reconnect loop is already in progress — nothing to do

        var loopCts = new CancellationTokenSource();
        _reconnectLoopCts = loopCts;
        var token = loopCts.Token;

        try
        {
            var attempt = 0;

            while (!token.IsCancellationRequested && !_userDisconnectRequested)
            {
                var delay = _reconnectPolicy.NextDelay(attempt);
                Logger.Info($"[Relay] Reconnecting in {delay.TotalSeconds:F1}s (attempt {attempt + 1})");

                try
                {
                    await Task.Delay(delay, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (token.IsCancellationRequested || _userDisconnectRequested)
                    break;

                // Token cooperation: never reconnect with an expired/near-expired JWT —
                // refresh first so the presence WS handshake doesn't fail on auth.
                await EnsureFreshTokenAsync();

                await ConnectPresenceAsync();

                if (IsConnected)
                {
                    Logger.Info("[Relay] Reconnected to relay after transport drop");
                    return;
                }

                attempt++;
            }

            // Loop ended without reconnecting (cancelled or user disconnected).
            SetState(RelayConnectionState.Disconnected);
        }
        finally
        {
            Interlocked.Exchange(ref _reconnectInFlight, 0);
            if (ReferenceEquals(_reconnectLoopCts, loopCts))
                _reconnectLoopCts = null;
            loopCts.Dispose();
        }
    }

    /// <summary>
    /// Refreshes the access token first if it's expired or within <see cref="TokenRefreshBuffer"/>
    /// of expiring. Cooperates with the existing TokenRefreshLoopAsync — this is a best-effort
    /// pre-check, not a replacement for it. No-op if expiry is unknown (best effort).
    /// </summary>
    private async Task EnsureFreshTokenAsync()
    {
        if (_tokenExpiresAt is null) return;
        if (DateTimeOffset.UtcNow + TokenRefreshBuffer < _tokenExpiresAt.Value) return;

        Logger.Info("[Relay] Access token near/at expiry before reconnect — refreshing first");
        await RefreshTokenAsync();
    }
}
