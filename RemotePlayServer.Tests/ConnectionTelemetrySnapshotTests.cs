using System.Text.Json;
using RemotePlayServer.Infrastructure.Network.Telemetry;

namespace RemotePlayServer.Tests;

/// <summary>
/// Asserts the Host's wire DTO matches telemetry-snapshot-contract-v1 exactly (field names,
/// casing, defaults) — Android and Relay implement the same contract independently, so a
/// silent field-name drift here would break cross-repo ingestion without any compiler error.
/// </summary>
public class ConnectionTelemetrySnapshotTests
{
    [Fact]
    public void Serialize_FullSnapshot_ProducesContractV1FieldNames()
    {
        var snapshot = new ConnectionTelemetrySnapshot
        {
            Event = "snapshot",
            SessionId = "abc123",
            PcRole = "video",
            MonitorIndex = 2,
            Generation = 1,
            Sequence = 5,
            LocalCandidateType = "relay",
            RemoteCandidateType = "srflx",
            AddressFamily = "ipv4",
            Protocol = "udp",
            RelayProtocol = "udp",
            PathClass = "relay",
            SendBitrateKbps = 8000,
            Codec = "h265",
            Width = 1920,
            Height = 1080,
            Fps = 60
        };

        var json = JsonSerializer.Serialize(snapshot);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(1, root.GetProperty("schema_version").GetInt32());
        Assert.Equal("host", root.GetProperty("source").GetString());
        Assert.Equal("snapshot", root.GetProperty("event").GetString());
        Assert.Equal("abc123", root.GetProperty("session_id").GetString());
        Assert.Equal("video", root.GetProperty("pc_role").GetString());
        Assert.Equal(2, root.GetProperty("monitor_index").GetInt32());
        Assert.Equal(1, root.GetProperty("generation").GetInt32());
        Assert.Equal(5, root.GetProperty("sequence").GetInt64());
        Assert.Equal("relay", root.GetProperty("local_candidate_type").GetString());
        Assert.Equal("srflx", root.GetProperty("remote_candidate_type").GetString());
        Assert.Equal("ipv4", root.GetProperty("address_family").GetString());
        Assert.Equal("udp", root.GetProperty("protocol").GetString());
        Assert.Equal("udp", root.GetProperty("relay_protocol").GetString());
        Assert.Equal("relay", root.GetProperty("path_class").GetString());
        Assert.Equal(8000, root.GetProperty("send_bitrate_kbps").GetInt32());
        Assert.Equal("h265", root.GetProperty("codec").GetString());
        Assert.Equal(1920, root.GetProperty("width").GetInt32());
        Assert.Equal(1080, root.GetProperty("height").GetInt32());
        Assert.Equal(60, root.GetProperty("fps").GetInt32());
    }

    [Fact]
    public void Serialize_WsSafeModeEvent_OmitsNullQoeAndPairFields()
    {
        var snapshot = new ConnectionTelemetrySnapshot
        {
            Event = "ws_safe_mode_enter",
            SessionId = "abc123",
            PcRole = "main",
            MonitorIndex = 0,
            Generation = 0,
            Sequence = 1
        };

        var options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
        var json = JsonSerializer.Serialize(snapshot, options);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("ws_safe_mode_enter", root.GetProperty("event").GetString());
        Assert.False(root.TryGetProperty("local_candidate_type", out _));
        Assert.False(root.TryGetProperty("rtt_ms", out _));
        Assert.False(root.TryGetProperty("codec", out _));
    }

    [Fact]
    public void Defaults_MatchContractV1()
    {
        var snapshot = new ConnectionTelemetrySnapshot { Event = "snapshot", PcRole = "main" };

        Assert.Equal(1, snapshot.SchemaVersion);
        Assert.Equal("host", snapshot.Source);
        Assert.Equal("unknown", snapshot.SessionId);
    }
}
