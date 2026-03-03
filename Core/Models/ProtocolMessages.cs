#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace RemotePlayServer.Core.Models
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
    /// Client -> Server: Hardware info acknowledgment with client codec capabilities.
    /// </summary>
    public class HardwareInfoAckMessage : ProtocolMessage
    {
        public override string Type => "hardware_info_ack";

        [JsonPropertyName("clientCodecs")]
        public ClientCodecCapability? ClientCodecs { get; set; }
    }

    /// <summary>
    /// Client codec capabilities for codec negotiation.
    /// </summary>
    public class ClientCodecCapability
    {
        [JsonPropertyName("supportedCodecs")]
        public string[]? SupportedCodecs { get; set; }

        [JsonPropertyName("preferredCodec")]
        public string PreferredCodec { get; set; } = "H264";

        [JsonPropertyName("supportsHevc")]
        public bool SupportsHevc { get; set; }

        [JsonPropertyName("supportsVP9")]
        public bool SupportsVP9 { get; set; }

        [JsonPropertyName("supportsVP8")]
        public bool SupportsVP8 { get; set; }

        [JsonPropertyName("deviceModel")]
        public string DeviceModel { get; set; } = "";

        [JsonPropertyName("apiLevel")]
        public int ApiLevel { get; set; }

        /// <summary>
        /// Client screen width in pixels (per-eye for VR headsets).
        /// Used by server to decide resize strategy.
        /// </summary>
        [JsonPropertyName("screenWidth")]
        public int ScreenWidth { get; set; }

        /// <summary>
        /// Client screen height in pixels (per-eye for VR headsets).
        /// Used by server to decide resize strategy.
        /// Screens below 1440p get 50% resize; 1440p+ get original frame.
        /// </summary>
        [JsonPropertyName("screenHeight")]
        public int ScreenHeight { get; set; }
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

        /// <summary>
        /// TOTAL bitrate in kbps for ALL monitors combined.
        /// Server divides this by monitor count for per-encoder bitrate.
        /// </summary>
        [JsonPropertyName("bitrateKbps")]
        public int BitrateKbps { get; set; } = 20000;

        [JsonPropertyName("fps")]
        public int Fps { get; set; } = 60;

        [JsonPropertyName("refreshRate")]
        public int RefreshRate { get; set; } = 60;

        [JsonPropertyName("reason")]
        public string Reason { get; set; } = "";

        /// <summary>
        /// Selected video codec (H264, H265, VP9, or VP8) based on negotiation.
        /// </summary>
        [JsonPropertyName("selectedCodec")]
        public string SelectedCodec { get; set; } = "H264";

        /// <summary>
        /// Connection type: "USB", "WiFi", "LAN", or "Internet".
        /// Used by client for UI display and quality rating.
        /// </summary>
        [JsonPropertyName("connectionType")]
        public string ConnectionType { get; set; } = "Unknown";

        /// <summary>
        /// Network test results (ping, jitter, bandwidth).
        /// Included so client can display accurate network info.
        /// </summary>
        [JsonPropertyName("networkInfo")]
        public NetworkInfoDto? NetworkInfo { get; set; }
    }

    /// <summary>
    /// Network info DTO for suggested_config message.
    /// </summary>
    public class NetworkInfoDto
    {
        [JsonPropertyName("pingMs")]
        public double PingMs { get; set; }

        [JsonPropertyName("jitterMs")]
        public double JitterMs { get; set; }

        [JsonPropertyName("bandwidthMbps")]
        public double BandwidthMbps { get; set; }

        /// <summary>
        /// True if connection is via USB Tethering (RNDIS).
        /// </summary>
        [JsonPropertyName("isUsbMode")]
        public bool IsUsbMode { get; set; }

        /// <summary>
        /// USB-specific ICMP latency in milliseconds (when isUsbMode is true).
        /// This is measured directly via ICMP ping to the USB gateway, not WebSocket.
        /// Typically < 1ms for USB connections.
        /// </summary>
        [JsonPropertyName("usbLatencyMs")]
        public double UsbLatencyMs { get; set; }

        /// <summary>
        /// USB interface version: "USB 2.0", "USB 3.0", or null if not USB mode.
        /// </summary>
        [JsonPropertyName("usbVersion")]
        public string? UsbVersion { get; set; }

        /// <summary>
        /// Estimated bandwidth for USB mode in Mbps (480 for USB 2.0, 5000 for USB 3.0).
        /// </summary>
        [JsonPropertyName("usbEstimatedBandwidthMbps")]
        public double UsbEstimatedBandwidthMbps { get; set; }
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

        /// <summary>
        /// Monitor type: "standard", "ultrawide", or "super_ultrawide".
        /// Ultrawide/Super Ultrawide creates a single VDD virtual display with
        /// "Show Only" topology instead of the normal multi-monitor extend mode.
        /// </summary>
        [JsonPropertyName("monitorType")]
        public string MonitorType { get; set; } = "standard";
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

    /// <summary>
    /// Server -> Client: Request client to trigger a reconnect.
    /// Used for dynamic codec fallback or recovery from fatal streaming errors.
    /// </summary>
    public class ReconnectRequestMessage : ProtocolMessage
    {
        public override string Type => "reconnect_request";

        [JsonPropertyName("reason")]
        public string Reason { get; set; } = "";

        [JsonPropertyName("suggestedCodec")]
        public string? SuggestedCodec { get; set; }
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

    /// <summary>
    /// Client -> Server: Pause streaming (stop capture/encode but keep connection).
    /// Used when client goes back to menu during streaming.
    /// </summary>
    public class PauseStreamingMessage : ProtocolMessage
    {
        public override string Type => "pause_streaming";
    }

    /// <summary>
    /// Client -> Server: Resume streaming (restart capture/encode).
    /// Used when client returns from menu to continue streaming.
    /// </summary>
    public class ResumeStreamingMessage : ProtocolMessage
    {
        public override string Type => "resume_streaming";
    }

    /// <summary>
    /// Client -> Server: Pause a specific monitor's streaming.
    /// Stops encoding for that monitor but keeps connection alive.
    /// </summary>
    public class PauseMonitorMessage : ProtocolMessage
    {
        public override string Type => "pause_monitor";

        /// <summary>
        /// Index of the monitor to pause (0-based).
        /// </summary>
        [JsonPropertyName("monitorIndex")]
        public int MonitorIndex { get; set; }
    }

    /// <summary>
    /// Client -> Server: Resume a specific monitor's streaming.
    /// Restarts encoding for that monitor and requests keyframe.
    /// </summary>
    public class ResumeMonitorMessage : ProtocolMessage
    {
        public override string Type => "resume_monitor";

        /// <summary>
        /// Index of the monitor to resume (0-based).
        /// </summary>
        [JsonPropertyName("monitorIndex")]
        public int MonitorIndex { get; set; }
    }

    /// <summary>
    /// Client -> Server: Update streaming config during Phase 3.
    /// Allows dynamic FPS and Bitrate changes without reconnection.
    /// Bitrate is TOTAL for all monitors combined.
    /// </summary>
    public class UpdateConfigMessage : ProtocolMessage
    {
        public override string Type => "update_config";

        /// <summary>
        /// New target FPS (optional, null = no change).
        /// </summary>
        [JsonPropertyName("fps")]
        public int? Fps { get; set; }

        /// <summary>
        /// New TOTAL bitrate in kbps for ALL monitors combined (optional, null = no change).
        /// Server will divide by monitor count for per-encoder bitrate.
        /// </summary>
        [JsonPropertyName("bitrateKbps")]
        public int? BitrateKbps { get; set; }

        /// <summary>
        /// New target output resolution height in pixels (optional, null = no change).
        /// Server will resize all frames to this height (e.g., 720, 1080, 1440).
        /// </summary>
        [JsonPropertyName("resolutionHeight")]
        public int? ResolutionHeight { get; set; }
    }

    /// <summary>
    /// Server -> Client: Acknowledgment that config was updated.
    /// </summary>
    public class ConfigUpdatedMessage : ProtocolMessage
    {
        public override string Type => "config_updated";

        [JsonPropertyName("fps")]
        public int Fps { get; set; }

        [JsonPropertyName("bitrateKbps")]
        public int BitrateKbps { get; set; }

        [JsonPropertyName("resolutionHeight")]
        public int ResolutionHeight { get; set; }

        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = "";
    }

    // ==================== Cursor Types ====================

    /// <summary>
    /// Cursor type enum matching Windows system cursors.
    /// </summary>
    public enum CursorType
    {
        Unknown = 0,
        Arrow = 1,       // IDC_ARROW (32512)
        IBeam = 2,       // IDC_IBEAM (32513) - text selection
        Wait = 3,        // IDC_WAIT (32514) - hourglass/loading
        Cross = 4,       // IDC_CROSS (32515) - crosshair
        SizeNWSE = 5,    // IDC_SIZENWSE (32642) - diagonal resize NW-SE
        SizeNESW = 6,    // IDC_SIZENESW (32643) - diagonal resize NE-SW
        SizeWE = 7,      // IDC_SIZEWE (32644) - horizontal resize
        SizeNS = 8,      // IDC_SIZENS (32645) - vertical resize
        SizeAll = 9,     // IDC_SIZEALL (32646) - move cursor
        No = 10,         // IDC_NO (32648) - not allowed
        Hand = 11,       // IDC_HAND (32649) - pointing hand (links)
        AppStarting = 12,// IDC_APPSTARTING (32650) - arrow + hourglass
        Help = 13,       // IDC_HELP (32651) - arrow + question mark
        UpArrow = 14,    // IDC_UPARROW (32516) - up arrow
        Custom = 99      // Application-specific custom cursor
    }

    /// <summary>
    /// Server -> Client: Cursor position update.
    /// Sent when cursor moves to a different position or monitor.
    /// </summary>
    public class CursorPositionMessage : ProtocolMessage
    {
        public override string Type => "cursor_position";

        /// <summary>
        /// Index of the monitor where cursor is located (-1 if not on any monitor).
        /// </summary>
        [JsonPropertyName("monitorIndex")]
        public int MonitorIndex { get; set; } = -1;

        /// <summary>
        /// Normalized X position within the monitor (0.0 to 1.0).
        /// </summary>
        [JsonPropertyName("u")]
        public float U { get; set; }

        /// <summary>
        /// Normalized Y position within the monitor (0.0 to 1.0).
        /// </summary>
        [JsonPropertyName("v")]
        public float V { get; set; }

        /// <summary>
        /// Whether the cursor is visible.
        /// </summary>
        [JsonPropertyName("visible")]
        public bool Visible { get; set; }

        /// <summary>
        /// Cursor type enum value.
        /// </summary>
        [JsonPropertyName("cursorType")]
        public int CursorTypeValue { get; set; } = (int)CursorType.Arrow;

        /// <summary>
        /// Unique cursor ID (hCursor handle as Int64).
        /// Client uses this to look up cached cursor texture.
        /// </summary>
        [JsonPropertyName("cursorId")]
        public long CursorId { get; set; } = 0;
    }

    /// <summary>
    /// Server -> Client: Cursor image data.
    /// Sent when cursor changes to a type not yet cached by client.
    /// </summary>
    public class CursorImageMessage : ProtocolMessage
    {
        public override string Type => "cursor_image";

        /// <summary>
        /// Unique cursor ID (hCursor handle as Int64).
        /// </summary>
        [JsonPropertyName("cursorId")]
        public long CursorId { get; set; }

        /// <summary>
        /// Cursor type enum value.
        /// </summary>
        [JsonPropertyName("cursorType")]
        public int CursorTypeValue { get; set; }

        /// <summary>
        /// Image width in pixels.
        /// </summary>
        [JsonPropertyName("width")]
        public int Width { get; set; }

        /// <summary>
        /// Image height in pixels.
        /// </summary>
        [JsonPropertyName("height")]
        public int Height { get; set; }

        /// <summary>
        /// Hotspot X offset in pixels from top-left.
        /// </summary>
        [JsonPropertyName("hotspotX")]
        public int HotspotX { get; set; }

        /// <summary>
        /// Hotspot Y offset in pixels from top-left.
        /// </summary>
        [JsonPropertyName("hotspotY")]
        public int HotspotY { get; set; }

        /// <summary>
        /// Base64-encoded PNG image data.
        /// </summary>
        [JsonPropertyName("imageBase64")]
        public string ImageBase64 { get; set; } = "";
    }

    // ==================== Adaptive FPS Messages ====================

    /// <summary>
    /// Client -> Server: FPS feedback for adaptive encoding.
    /// Client reports effective rendering FPS so server can adjust encoding rate.
    /// </summary>
    public class FpsFeedbackMessage : ProtocolMessage
    {
        public override string Type => "fps_feedback";

        [JsonPropertyName("monitorIndex")]
        public int MonitorIndex { get; set; }

        [JsonPropertyName("effectiveFps")]
        public float EffectiveFps { get; set; }

        [JsonPropertyName("renderedFrames")]
        public int RenderedFrames { get; set; }

        [JsonPropertyName("totalFrames")]
        public long TotalFrames { get; set; }  // Cumulative frames since stream start (for pipeline comparison)

        [JsonPropertyName("droppedFrames")]
        public int DroppedFrames { get; set; }
    }

    /// <summary>
    /// Server -> Client: Acknowledgment of FPS adjustment.
    /// </summary>
    public class FpsAdjustedMessage : ProtocolMessage
    {
        public override string Type => "fps_adjusted";

        [JsonPropertyName("monitorIndex")]
        public int MonitorIndex { get; set; }

        [JsonPropertyName("targetFps")]
        public int TargetFps { get; set; }
    }

    // ==================== Adaptive Bitrate Messages ====================

    /// <summary>
    /// Per-monitor quality feedback data.
    /// </summary>
    public class MonitorFeedback
    {
        [JsonPropertyName("index")]
        public int Index { get; set; }

        [JsonPropertyName("renderedFrames")]
        public int RenderedFrames { get; set; }

        [JsonPropertyName("realFrames")]
        public int RealFrames { get; set; }

        [JsonPropertyName("droppedFrames")]
        public int DroppedFrames { get; set; }

        [JsonPropertyName("texturePtrWorking")]
        public bool TexturePtrWorking { get; set; }
    }

    /// <summary>
    /// Client -> Server: Comprehensive quality feedback for adaptive bitrate.
    /// Sent periodically (every ~3 seconds) to enable server-side bitrate adaptation.
    /// </summary>
    public class QualityFeedbackMessage : ProtocolMessage
    {
        public override string Type => "quality_feedback";

        [JsonPropertyName("timestamp")]
        public long Timestamp { get; set; }

        [JsonPropertyName("rttMs")]
        public float RttMs { get; set; }

        [JsonPropertyName("avgRttMs")]
        public float AvgRttMs { get; set; }

        [JsonPropertyName("jitterMs")]
        public float JitterMs { get; set; }

        [JsonPropertyName("packetLossRate")]
        public float PacketLossRate { get; set; }

        [JsonPropertyName("avgPacketLossRate")]
        public float AvgPacketLossRate { get; set; }

        [JsonPropertyName("effectiveFps")]
        public float EffectiveFps { get; set; }

        [JsonPropertyName("targetFps")]
        public float TargetFps { get; set; }

        [JsonPropertyName("frameLatencyMs")]
        public float FrameLatencyMs { get; set; }

        [JsonPropertyName("bufferStatus")]
        public string BufferStatus { get; set; } = "healthy"; // "healthy", "starving", "lossy", "high_latency", "overflow"

        [JsonPropertyName("connectionHealth")]
        public int ConnectionHealth { get; set; }

        [JsonPropertyName("isWiFi")]
        public bool IsWiFi { get; set; }

        [JsonPropertyName("monitors")]
        public List<MonitorFeedback>? Monitors { get; set; }
    }

    /// <summary>
    /// Server -> Client: Bitrate adjustment notification.
    /// Sent in response to quality_feedback when bitrate was changed.
    /// </summary>
    public class BitrateAdjustedMessage : ProtocolMessage
    {
        public override string Type => "bitrate_adjusted";

        [JsonPropertyName("monitorIndex")]
        public int MonitorIndex { get; set; }

        [JsonPropertyName("bitrateKbps")]
        public int BitrateKbps { get; set; }

        [JsonPropertyName("reason")]
        public string Reason { get; set; } = "";
    }

    /// <summary>
    /// Server -> Client: Quality recommendation.
    /// Sent when sustained poor quality suggests resolution/fps changes.
    /// </summary>
    public class QualityRecommendationMessage : ProtocolMessage
    {
        public override string Type => "quality_recommendation";

        [JsonPropertyName("recommendation")]
        public string Recommendation { get; set; } = ""; // "reduce_fps", "reduce_resolution", "reduce_bitrate"

        [JsonPropertyName("reason")]
        public string Reason { get; set; } = "";
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
            WriteIndented = false,
            // CRITICAL: Prevent escaping of '+' in base64 strings
            // Default encoder escapes '+' to '\u002B' which corrupts base64 data
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
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
