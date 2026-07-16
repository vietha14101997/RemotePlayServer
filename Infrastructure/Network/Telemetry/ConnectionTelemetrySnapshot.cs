#nullable enable
using System.Text.Json.Serialization;

namespace RemotePlayServer.Infrastructure.Network.Telemetry;

/// <summary>
/// Host-side wire DTO for the shared telemetry-snapshot-contract-v1 (see
/// plans/260715-2315-wan-p2p-turn-quality-hardening/telemetry-snapshot-contract-v1.md).
/// Field names/casing MUST match the contract exactly — Android and Relay independently
/// implement the same schema. Every field the Host cannot actually read is left null and
/// omitted from the wire JSON (see HostTelemetryReporter's serializer options) rather than
/// fabricated; nullable QoE fields are entirely optional per the contract.
/// </summary>
public sealed class ConnectionTelemetrySnapshot
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("source")]
    public string Source { get; init; } = "host";

    /// <summary>snapshot | path_transition | ws_safe_mode_enter | ws_safe_mode_exit</summary>
    [JsonPropertyName("event")]
    public required string Event { get; init; }

    /// <summary>Opaque per-connection token (Host uses PhaseProtocolHandler's clientId GUID). "unknown" if unavailable.</summary>
    [JsonPropertyName("session_id")]
    public string SessionId { get; init; } = "unknown";

    /// <summary>main | video</summary>
    [JsonPropertyName("pc_role")]
    public required string PcRole { get; init; }

    [JsonPropertyName("monitor_index")]
    public int MonitorIndex { get; init; }

    [JsonPropertyName("generation")]
    public int Generation { get; init; }

    [JsonPropertyName("sequence")]
    public long Sequence { get; init; }

    // --- selected candidate pair (present when event=snapshot/path_transition) ---

    [JsonPropertyName("local_candidate_type")]
    public string? LocalCandidateType { get; init; }

    [JsonPropertyName("remote_candidate_type")]
    public string? RemoteCandidateType { get; init; }

    [JsonPropertyName("address_family")]
    public string? AddressFamily { get; init; }

    [JsonPropertyName("protocol")]
    public string? Protocol { get; init; }

    [JsonPropertyName("relay_protocol")]
    public string? RelayProtocol { get; init; }

    [JsonPropertyName("path_class")]
    public string? PathClass { get; init; }

    // --- QoE metrics (nullable; omitted when unavailable — Phase 00 Host does not measure
    //     rtt/jitter/loss/qp/drops/ttff/freeze yet, so those stay null by design) ---

    [JsonPropertyName("rtt_ms")]
    public int? RttMs { get; init; }

    [JsonPropertyName("jitter_ms")]
    public int? JitterMs { get; init; }

    [JsonPropertyName("loss_pct")]
    public double? LossPct { get; init; }

    [JsonPropertyName("send_bitrate_kbps")]
    public int? SendBitrateKbps { get; init; }

    [JsonPropertyName("available_bitrate_kbps")]
    public int? AvailableBitrateKbps { get; init; }

    [JsonPropertyName("codec")]
    public string? Codec { get; init; }

    [JsonPropertyName("width")]
    public int? Width { get; init; }

    [JsonPropertyName("height")]
    public int? Height { get; init; }

    [JsonPropertyName("fps")]
    public int? Fps { get; init; }

    [JsonPropertyName("qp")]
    public int? Qp { get; init; }

    [JsonPropertyName("frame_drops")]
    public int? FrameDrops { get; init; }

    [JsonPropertyName("ttff_ms")]
    public int? TtffMs { get; init; }

    [JsonPropertyName("freeze_count")]
    public int? FreezeCount { get; init; }

    [JsonPropertyName("freeze_ms_total")]
    public int? FreezeMsTotal { get; init; }
}
