#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using RemotePlayServer.Utils;

namespace RemotePlayServer.Protocol
{
    /// <summary>
    /// Base class for all protocol messages.
    /// </summary>
    public abstract class ProtocolMessage
    {
        [JsonPropertyName("type")]
        public abstract string Type { get; }
    }

    // ==================== Phase 1 Messages ====================

    /// <summary>
    /// Server -> Client: Hardware info sent immediately after WS connect.
    /// </summary>
    public class HardwareInfoMessage : ProtocolMessage
    {
        public override string Type => "hardware_info";

        [JsonPropertyName("device")]
        public DeviceInfo Device { get; set; } = new();

        [JsonPropertyName("encoder")]
        public EncoderInfo Encoder { get; set; } = new();

        [JsonPropertyName("monitors")]
        public List<MonitorInfoDto> Monitors { get; set; } = new();
    }

    public class DeviceInfo
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("processor")]
        public string Processor { get; set; } = "";

        [JsonPropertyName("gpu")]
        public string Gpu { get; set; } = "";

        [JsonPropertyName("gpuVramGB")]
        public int GpuVramGB { get; set; }

        [JsonPropertyName("ramGB")]
        public int RamGB { get; set; }

        [JsonPropertyName("os")]
        public string Os { get; set; } = "";
    }

    /// <summary>
    /// Server -> Client: Notify client that speed test is starting.
    /// </summary>
    public class SpeedTestStartMessage : ProtocolMessage
    {
        public override string Type => "speedtest_start";

        [JsonPropertyName("direction")]
        public string Direction { get; set; } = "download"; // "download" or "upload"

        [JsonPropertyName("chunkSize")]
        public int ChunkSize { get; set; } = 64 * 1024;

        [JsonPropertyName("durationMs")]
        public int DurationMs { get; set; } = 2000;
    }

    /// <summary>
    /// Client -> Server: Request speed test data (for download/upload measurement).
    /// </summary>
    public class SpeedTestRequestMessage : ProtocolMessage
    {
        public override string Type => "speedtest_request";

        [JsonPropertyName("direction")]
        public string Direction { get; set; } = "download"; // "download" or "upload"

        [JsonPropertyName("durationMs")]
        public int DurationMs { get; set; } = 2000;
    }

    /// <summary>
    /// Client -> Server: Speed test result from client-side measurement.
    /// </summary>
    public class SpeedTestResultMessage : ProtocolMessage
    {
        public override string Type => "speedtest_result";

        [JsonPropertyName("bandwidthMbps")]
        public double BandwidthMbps { get; set; }

        [JsonPropertyName("pingMs")]
        public double PingMs { get; set; }

        [JsonPropertyName("jitterMs")]
        public double JitterMs { get; set; }
    }

    /// <summary>
    /// Server -> Client: Speed test completed, here are the results.
    /// </summary>
    public class NetworkInfoMessage : ProtocolMessage
    {
        public override string Type => "network_info";

        [JsonPropertyName("pingMs")]
        public double PingMs { get; set; }

        [JsonPropertyName("jitterMs")]
        public double JitterMs { get; set; }

        [JsonPropertyName("bandwidthMbps")]
        public double BandwidthMbps { get; set; }

        [JsonPropertyName("connectionType")]
        public string ConnectionType { get; set; } = "Unknown";
    }

    /// <summary>
    /// Server -> Client: Suggested configuration based on hardware/network.
    /// </summary>
    public class SuggestedConfigMessage : ProtocolMessage
    {
        public override string Type => "suggested_config";

        [JsonPropertyName("monitors")]
        public int Monitors { get; set; } = 3;

        [JsonPropertyName("resolution")]
        public ResolutionDto Resolution { get; set; } = new();

        [JsonPropertyName("bitrateKbps")]
        public int BitrateKbps { get; set; } = 20000;

        [JsonPropertyName("fps")]
        public int Fps { get; set; } = 60;

        [JsonPropertyName("refreshRate")]
        public int RefreshRate { get; set; } = 60;

        [JsonPropertyName("reason")]
        public string Reason { get; set; } = "";
    }

    public class ResolutionDto
    {
        [JsonPropertyName("w")]
        public int Width { get; set; } = 1920;

        [JsonPropertyName("h")]
        public int Height { get; set; } = 1080;
    }

    /// <summary>
    /// Client -> Server: Ready to proceed to next phase.
    /// </summary>
    public class ProceedMessage : ProtocolMessage
    {
        public override string Type => "proceed";

        [JsonPropertyName("phase")]
        public int Phase { get; set; }
    }

    // ==================== Phase 2 Messages ====================

    /// <summary>
    /// Client -> Server: Display configuration request.
    /// </summary>
    public class DisplayConfigMessage : ProtocolMessage
    {
        public override string Type => "display_config";

        [JsonPropertyName("monitors")]
        public int Monitors { get; set; } = 3;

        [JsonPropertyName("resolution")]
        public ResolutionDto Resolution { get; set; } = new();

        [JsonPropertyName("refreshRate")]
        public int RefreshRate { get; set; } = 60;

        [JsonPropertyName("bitrateKbps")]
        public int BitrateKbps { get; set; } = 20000;

        [JsonPropertyName("fps")]
        public int Fps { get; set; } = 60;

        [JsonPropertyName("preferGpu")]
        public string? PreferGpu { get; set; }
    }

    /// <summary>
    /// Server -> Client: Configuration progress update.
    /// </summary>
    public class ConfigProgressMessage : ProtocolMessage
    {
        public override string Type => "config_progress";

        [JsonPropertyName("step")]
        public string Step { get; set; } = ""; // vdd_setup, resolution, topology, capture_init

        [JsonPropertyName("progress")]
        public int Progress { get; set; } // 0-100

        [JsonPropertyName("message")]
        public string Message { get; set; } = "";
    }

    /// <summary>
    /// Server -> Client: Configuration complete, ready for ICE.
    /// </summary>
    public class ConfigCompleteMessage : ProtocolMessage
    {
        public override string Type => "config_complete";

        [JsonPropertyName("monitors")]
        public List<MonitorInfoDto> Monitors { get; set; } = new();

        [JsonPropertyName("captureReady")]
        public bool CaptureReady { get; set; }
    }

    /// <summary>
    /// Client -> Server: WebRTC offer for a specific monitor.
    /// </summary>
    public class OfferMessage : ProtocolMessage
    {
        public override string Type => "offer";

        [JsonPropertyName("monitorIndex")]
        public int MonitorIndex { get; set; }

        [JsonPropertyName("sdp")]
        public string Sdp { get; set; } = "";
    }

    /// <summary>
    /// Server -> Client: WebRTC answer for a specific monitor.
    /// </summary>
    public class AnswerMessage : ProtocolMessage
    {
        public override string Type => "answer";

        [JsonPropertyName("monitorIndex")]
        public int MonitorIndex { get; set; }

        [JsonPropertyName("sdp")]
        public string Sdp { get; set; } = "";
    }

    /// <summary>
    /// Bidirectional: ICE candidate for a specific monitor.
    /// </summary>
    public class CandidateMessage : ProtocolMessage
    {
        public override string Type => "candidate";

        [JsonPropertyName("monitorIndex")]
        public int MonitorIndex { get; set; }

        [JsonPropertyName("candidate")]
        public string Candidate { get; set; } = "";
    }

    /// <summary>
    /// Bidirectional: End of ICE candidates for a monitor.
    /// </summary>
    public class EndOfCandidatesMessage : ProtocolMessage
    {
        public override string Type => "end_of_candidates";

        [JsonPropertyName("monitorIndex")]
        public int MonitorIndex { get; set; }
    }

    /// <summary>
    /// Server -> Client: All ICE connections are ready.
    /// Sent when all PeerConnections have established ICE connectivity.
    /// </summary>
    public class IceReadyMessage : ProtocolMessage
    {
        public override string Type => "ice_ready";

        [JsonPropertyName("monitorCount")]
        public int MonitorCount { get; set; }
    }

    // ==================== Phase 3 Messages ====================

    /// <summary>
    /// Client -> Server: Start streaming command.
    /// </summary>
    public class StartStreamingMessage : ProtocolMessage
    {
        public override string Type => "start_streaming";
    }

    /// <summary>
    /// Server -> Client: Streaming has started.
    /// </summary>
    public class StreamingStartedMessage : ProtocolMessage
    {
        public override string Type => "streaming_started";

        [JsonPropertyName("timestamp")]
        public long Timestamp { get; set; }
    }

    /// <summary>
    /// Client -> Server: Stop streaming command.
    /// </summary>
    public class StopStreamingMessage : ProtocolMessage
    {
        public override string Type => "stop_streaming";
    }

    // ==================== Common Messages ====================

    /// <summary>
    /// Bidirectional: Ping message for keepalive.
    /// </summary>
    public class PingMessage : ProtocolMessage
    {
        public override string Type => "ping";
    }

    /// <summary>
    /// Bidirectional: Pong response to ping.
    /// </summary>
    public class PongMessage : ProtocolMessage
    {
        public override string Type => "pong";
    }

    /// <summary>
    /// Server -> Client: Error message.
    /// </summary>
    public class ErrorMessage : ProtocolMessage
    {
        public override string Type => "error";

        [JsonPropertyName("phase")]
        public int Phase { get; set; }

        [JsonPropertyName("code")]
        public string Code { get; set; } = "";

        [JsonPropertyName("message")]
        public string Message { get; set; } = "";
    }

    // ==================== Message Parser ====================

    /// <summary>
    /// Utility class for parsing and serializing protocol messages.
    /// </summary>
    public static class ProtocolMessageParser
    {
        private static readonly JsonSerializerOptions _options = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        /// <summary>
        /// Serialize a message to JSON string.
        /// </summary>
        public static string Serialize<T>(T message) where T : ProtocolMessage
        {
            return JsonSerializer.Serialize(message, _options);
        }

        /// <summary>
        /// Try to parse message type from JSON string.
        /// </summary>
        public static string? GetMessageType(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("type", out var typeProp))
                {
                    return typeProp.GetString();
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Parse a JSON string into a specific message type.
        /// </summary>
        public static T? Parse<T>(string json) where T : class
        {
            try
            {
                return JsonSerializer.Deserialize<T>(json, _options);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Parse a JSON string and return the raw JsonDocument for manual parsing.
        /// </summary>
        public static JsonDocument? ParseDocument(string json)
        {
            try
            {
                return JsonDocument.Parse(json);
            }
            catch
            {
                return null;
            }
        }
    }
}
