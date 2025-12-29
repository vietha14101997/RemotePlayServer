#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RemotePlayServer.Utils
{
    /// <summary>
    /// Comprehensive hardware and network information for the server machine.
    /// Sent to client during Phase 1 of connection protocol.
    /// </summary>
    public class HardwareInfo
    {
        [JsonPropertyName("deviceName")]
        public string DeviceName { get; set; } = "";

        [JsonPropertyName("processor")]
        public ProcessorInfo Processor { get; set; } = new();

        [JsonPropertyName("gpu")]
        public GpuInfo Gpu { get; set; } = new();

        [JsonPropertyName("ram")]
        public RamInfo Ram { get; set; } = new();

        [JsonPropertyName("os")]
        public OsInfo Os { get; set; } = new();

        [JsonPropertyName("network")]
        public NetworkAdapterInfo Network { get; set; } = new();

        [JsonPropertyName("timestamp")]
        public long Timestamp { get; set; }
    }

    public class ProcessorInfo
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("cores")]
        public int Cores { get; set; }

        [JsonPropertyName("logicalProcessors")]
        public int LogicalProcessors { get; set; }

        [JsonPropertyName("speedMHz")]
        public int SpeedMHz { get; set; }
    }

    public class GpuInfo
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("vendor")]
        public string Vendor { get; set; } = "Unknown"; // NVIDIA, AMD, Intel, Unknown

        [JsonPropertyName("vendorId")]
        public uint VendorId { get; set; }

        [JsonPropertyName("vramMB")]
        public long VramMB { get; set; }

        [JsonPropertyName("vramGB")]
        public double VramGB => Math.Round(VramMB / 1024.0, 1);

        [JsonPropertyName("driverVersion")]
        public string DriverVersion { get; set; } = "";
    }

    public class RamInfo
    {
        [JsonPropertyName("totalMB")]
        public long TotalMB { get; set; }

        [JsonPropertyName("totalGB")]
        public double TotalGB => Math.Round(TotalMB / 1024.0, 1);

        [JsonPropertyName("availableMB")]
        public long AvailableMB { get; set; }
    }

    public class OsInfo
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("version")]
        public string Version { get; set; } = "";

        [JsonPropertyName("build")]
        public string Build { get; set; } = "";

        [JsonPropertyName("architecture")]
        public string Architecture { get; set; } = "";
    }

    public class NetworkAdapterInfo
    {
        [JsonPropertyName("connectionType")]
        public string ConnectionType { get; set; } = "Unknown"; // Ethernet, WiFi, Unknown

        [JsonPropertyName("adapterName")]
        public string AdapterName { get; set; } = "";

        [JsonPropertyName("speedMbps")]
        public long SpeedMbps { get; set; }

        [JsonPropertyName("ipAddress")]
        public string IpAddress { get; set; } = "";
    }

    /// <summary>
    /// Encoder capabilities information.
    /// </summary>
    public class EncoderInfo
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "Unknown"; // NVENC, AMF, QSV, Software

        [JsonPropertyName("hwAccel")]
        public bool HwAccel { get; set; }

        [JsonPropertyName("maxWidth")]
        public int MaxWidth { get; set; } = 4096;

        [JsonPropertyName("maxHeight")]
        public int MaxHeight { get; set; } = 4096;

        [JsonPropertyName("maxBitrateKbps")]
        public int MaxBitrateKbps { get; set; } = 100000;

        /// <summary>
        /// List of supported video codecs (e.g., ["H264", "H265"])
        /// </summary>
        [JsonPropertyName("supportedCodecs")]
        public List<string> SupportedCodecs { get; set; } = new() { "H264" };

        /// <summary>
        /// Preferred codec (H264 or H265) - H265 provides ~30-50% better quality at same bitrate
        /// </summary>
        [JsonPropertyName("preferredCodec")]
        public string PreferredCodec { get; set; } = "H265";

        /// <summary>
        /// True if server supports H.265/HEVC encoding
        /// </summary>
        [JsonPropertyName("supportsHevc")]
        public bool SupportsHevc { get; set; } = false;
    }

    /// <summary>
    /// Monitor information for display configuration.
    /// </summary>
    public class MonitorInfoDto
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("width")]
        public int Width { get; set; }

        [JsonPropertyName("height")]
        public int Height { get; set; }

        [JsonPropertyName("refreshRate")]
        public int RefreshRate { get; set; }

        [JsonPropertyName("isVirtual")]
        public bool IsVirtual { get; set; }
    }

    /// <summary>
    /// Network diagnostics measured per-connection (ping, bandwidth).
    /// </summary>
    public class NetworkDiagnostics
    {
        [JsonPropertyName("pingMs")]
        public double PingMs { get; set; }

        [JsonPropertyName("jitterMs")]
        public double JitterMs { get; set; }

        [JsonPropertyName("bandwidthMbps")]
        public double BandwidthMbps { get; set; }

        [JsonPropertyName("packetLossPercent")]
        public double PacketLossPercent { get; set; }

        [JsonPropertyName("connectionType")]
        public string ConnectionType { get; set; } = "Unknown"; // LAN, WiFi, Internet
    }

    /// <summary>
    /// Suggested streaming configuration based on hardware/network analysis.
    /// </summary>
    public class SuggestedConfig
    {
        [JsonPropertyName("monitors")]
        public int Monitors { get; set; } = 3;

        [JsonPropertyName("resolutionWidth")]
        public int ResolutionWidth { get; set; } = 1920;

        [JsonPropertyName("resolutionHeight")]
        public int ResolutionHeight { get; set; } = 1080;

        [JsonPropertyName("bitrateKbps")]
        public int BitrateKbps { get; set; } = 20000;

        [JsonPropertyName("fps")]
        public int Fps { get; set; } = 60;

        [JsonPropertyName("refreshRate")]
        public int RefreshRate { get; set; } = 60;

        [JsonPropertyName("reason")]
        public string Reason { get; set; } = "";
    }
}
