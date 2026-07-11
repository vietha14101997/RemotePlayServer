#nullable enable
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using RemotePlayServer.Core;
using RemotePlayServer.Core.Models;

namespace RemotePlayServer.Infrastructure.Network;

/// <summary>
/// Mints ephemeral TURN credentials for a coturn server running in
/// use-auth-secret (REST API) mode, and assembles the per-session ICE server
/// list shared by the host's own PeerConnections and the config_complete
/// message sent to the client. Mirrors the relay's Go implementation
/// (RelaySignalingServer/internal/turn/credentials.go):
///   username   = "{unixExpiry}:{userId}"
///   credential = Base64(HMAC-SHA1(secret, username))
/// Minting locally means TURN keeps working when the relay server is down
/// and over LAN/tunnel signaling paths that never touch the relay.
/// </summary>
public static class TurnCredentialProvider
{
    private static InternetConfig? _config;

    /// <summary>Install the loaded internet config (call once at server start).</summary>
    public static void Configure(InternetConfig? config) => _config = config;

    public static bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_config?.TurnHost) &&
        !string.IsNullOrWhiteSpace(_config?.TurnSecret);

    /// <summary>Configured coturn host (for TURN-vs-P2P detection registration). Null when unset.</summary>
    public static string? ConfiguredTurnHost =>
        string.IsNullOrWhiteSpace(_config?.TurnHost) ? null : _config!.TurnHost;

    /// <summary>
    /// Full ICE server list for the current session: locally-minted coturn
    /// credentials first (if configured), then whatever the relay handed out.
    /// Used both for the host's RTCConfiguration and for the client via
    /// config_complete, so both ends allocate on the same TURN server.
    /// </summary>
    public static List<IceServerDto> GetSessionIceServers()
    {
        var list = new List<IceServerDto>();

        var minted = MintIceServer();
        if (minted != null)
            list.Add(minted);

        var relayIce = RelayClientManager.Instance?.IceServers;
        if (relayIce != null)
        {
            foreach (var ice in relayIce)
            {
                list.Add(new IceServerDto
                {
                    Urls = ice.Urls,
                    Username = ice.Username,
                    Credential = ice.Credential
                });
            }
        }
        return list;
    }

    /// <summary>
    /// Mint one ephemeral TURN entry (UDP + TCP urls) from the configured
    /// static-auth-secret. Null when TURN is not configured.
    /// </summary>
    public static IceServerDto? MintIceServer(string userId = "host")
    {
        var cfg = _config;
        if (cfg == null ||
            string.IsNullOrWhiteSpace(cfg.TurnHost) ||
            string.IsNullOrWhiteSpace(cfg.TurnSecret))
            return null;

        var port = cfg.TurnPort > 0 ? cfg.TurnPort : 3478;
        var ttlHours = cfg.TurnTtlHours > 0 ? cfg.TurnTtlHours : 24;
        var expiry = DateTimeOffset.UtcNow.AddHours(ttlHours).ToUnixTimeSeconds();
        var username = $"{expiry}:{userId}";

        return new IceServerDto
        {
            Urls = new List<string>
            {
                $"turn:{cfg.TurnHost}:{port}?transport=udp",
                $"turn:{cfg.TurnHost}:{port}?transport=tcp"
            },
            Username = username,
            Credential = ComputeCredential(cfg.TurnSecret!, username)
        };
    }

    /// <summary>Base64(HMAC-SHA1(secret, username)) — coturn use-auth-secret scheme.</summary>
    public static string ComputeCredential(string secret, string username)
    {
        // Fully qualified: bare "Encoding" resolves to the sibling namespace
        // RemotePlayServer.Infrastructure.Encoding, shadowing System.Text.Encoding.
        using var hmac = new HMACSHA1(System.Text.Encoding.UTF8.GetBytes(secret));
        return Convert.ToBase64String(hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(username)));
    }
}
