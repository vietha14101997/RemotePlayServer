#nullable enable
using System.Text.Json.Serialization;

namespace RemotePlayServer.Core.Models;

/// <summary>
/// Configuration for internet remote mode (Cloudflare Tunnel).
/// Loaded from Configuration/internet-settings.json.
/// </summary>
public class InternetConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;

    [JsonPropertyName("turnServerUrl")]
    public string? TurnServerUrl { get; set; }

    [JsonPropertyName("turnUsername")]
    public string? TurnUsername { get; set; }

    [JsonPropertyName("turnPassword")]
    public string? TurnPassword { get; set; }

    /// <summary>
    /// coturn host (IP or domain) for locally-minted ephemeral credentials
    /// (static-auth-secret / REST scheme). Independent of the relay server —
    /// TURN keeps working even when the relay is down.
    /// </summary>
    [JsonPropertyName("turnHost")]
    public string? TurnHost { get; set; }

    [JsonPropertyName("turnPort")]
    public int TurnPort { get; set; } = 3478;

    /// <summary>Must match coturn's static-auth-secret. Never sent to clients — only ephemeral credentials are.</summary>
    [JsonPropertyName("turnSecret")]
    public string? TurnSecret { get; set; }

    /// <summary>Lifetime of minted TURN credentials; must outlast the longest expected session.</summary>
    [JsonPropertyName("turnTtlHours")]
    public int TurnTtlHours { get; set; } = 24;

    [JsonPropertyName("requireToken")]
    public bool RequireToken { get; set; } = true;

    [JsonPropertyName("relayUrl")]
    public string? RelayUrl { get; set; }

    [JsonPropertyName("relayEmail")]
    public string? RelayEmail { get; set; }

    [JsonPropertyName("relayPassword")]
    public string? RelayPassword { get; set; }

    [JsonPropertyName("useRelay")]
    public bool UseRelay { get; set; } = false;
}
