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

    [JsonPropertyName("requireToken")]
    public bool RequireToken { get; set; } = true;
}
