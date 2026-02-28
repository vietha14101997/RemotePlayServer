using RemotePlayServer.Core.Models;
using RemotePlayServer.Infrastructure.Network;

namespace RemotePlayServer.Tests;

public class StreamingOptimizerTests
{
    private static HardwareInfo DefaultHardware() => new()
    {
        DeviceName = "TestPC",
        Processor = new ProcessorInfo { Name = "Intel i9", Cores = 16 },
        Gpu = new GpuInfo { Name = "RTX 4090", Vendor = "NVIDIA", VramMB = 24576 },
        Ram = new RamInfo { TotalMB = 65536 }
    };

    private static EncoderInfo HwEncoder() => new()
    {
        Type = "NVENC",
        HwAccel = true,
        SupportedCodecs = new List<string> { "H264", "H265", "VP9" }
    };

    private static EncoderInfo SwEncoder() => new()
    {
        Type = "Software",
        HwAccel = false,
        SupportedCodecs = new List<string> { "H264", "VP9" }
    };

    // ==================== Resolution (Always 1440x810) ====================

    [Fact]
    public void CalculateSuggestedConfig_AlwaysReturns1440x810()
    {
        var config = StreamingOptimizer.CalculateSuggestedConfig(
            DefaultHardware(), HwEncoder(),
            new SpeedTestResult { PingMs = 5, BandwidthMbps = 500, Success = true });

        Assert.Equal(1440, config.ResolutionWidth);
        Assert.Equal(810, config.ResolutionHeight);
    }

    // ==================== LAN Connection ====================

    [Fact]
    public void CalculateSuggestedConfig_LAN_MaxBitrate()
    {
        var config = StreamingOptimizer.CalculateSuggestedConfig(
            DefaultHardware(), HwEncoder(),
            new SpeedTestResult { PingMs = 2, BandwidthMbps = 900, Success = true });

        // LAN: ping < 5ms && bandwidth > 500Mbps → 40Mbps max
        Assert.Equal(40000, config.BitrateKbps);
    }

    [Fact]
    public void CalculateSuggestedConfig_LAN_HwEncoder_60Fps()
    {
        var config = StreamingOptimizer.CalculateSuggestedConfig(
            DefaultHardware(), HwEncoder(),
            new SpeedTestResult { PingMs = 2, BandwidthMbps = 900, Success = true });

        Assert.Equal(60, config.Fps);
    }

    [Fact]
    public void CalculateSuggestedConfig_LAN_3Monitors()
    {
        var config = StreamingOptimizer.CalculateSuggestedConfig(
            DefaultHardware(), HwEncoder(),
            new SpeedTestResult { PingMs = 2, BandwidthMbps = 900, Success = true });

        Assert.Equal(3, config.Monitors);
    }

    // ==================== WiFi Connection ====================

    [Fact]
    public void CalculateSuggestedConfig_WiFi_GoodBandwidth()
    {
        var config = StreamingOptimizer.CalculateSuggestedConfig(
            DefaultHardware(), HwEncoder(),
            new SpeedTestResult { PingMs = 10, BandwidthMbps = 300, Success = true });

        // WiFi with good bandwidth: should get reasonable bitrate
        Assert.True(config.BitrateKbps >= 15000);
        Assert.True(config.BitrateKbps <= 40000);
    }

    [Fact]
    public void CalculateSuggestedConfig_WiFi_HwEncoder_LowPing_60Fps()
    {
        var config = StreamingOptimizer.CalculateSuggestedConfig(
            DefaultHardware(), HwEncoder(),
            new SpeedTestResult { PingMs = 10, BandwidthMbps = 300, Success = true });

        // HwAccel + ping < 20ms → 60 FPS
        Assert.Equal(60, config.Fps);
    }

    [Fact]
    public void CalculateSuggestedConfig_WiFi_HwEncoder_MediumPing_45Fps()
    {
        var config = StreamingOptimizer.CalculateSuggestedConfig(
            DefaultHardware(), HwEncoder(),
            new SpeedTestResult { PingMs = 30, BandwidthMbps = 300, Success = true });

        // HwAccel + 20ms < ping < 50ms → 45 FPS
        Assert.Equal(45, config.Fps);
    }

    // ==================== Internet / Poor Connection ====================

    [Fact]
    public void CalculateSuggestedConfig_HighPing_SoftwareEncoder_30Fps()
    {
        var config = StreamingOptimizer.CalculateSuggestedConfig(
            DefaultHardware(), SwEncoder(),
            new SpeedTestResult { PingMs = 60, BandwidthMbps = 50, Success = true });

        // Software encoder → always 30 FPS
        Assert.Equal(30, config.Fps);
    }

    [Fact]
    public void CalculateSuggestedConfig_LowBandwidth_ReducesMonitors()
    {
        var config = StreamingOptimizer.CalculateSuggestedConfig(
            DefaultHardware(), HwEncoder(),
            new SpeedTestResult { PingMs = 20, BandwidthMbps = 30, Success = true });

        // 30 Mbps total, 3 monitors at 15Mbps each = 45Mbps needed
        // Available = 30 * 0.7 = 21Mbps < 45Mbps
        Assert.True(config.Monitors < 3);
    }

    // ==================== Bitrate Rounding ====================

    [Fact]
    public void CalculateSuggestedConfig_BitrateRoundedToNearestOption()
    {
        var config = StreamingOptimizer.CalculateSuggestedConfig(
            DefaultHardware(), HwEncoder(),
            new SpeedTestResult { PingMs = 15, BandwidthMbps = 200, Success = true });

        // Bitrate should be one of: 15000, 20000, 25000, 30000, 40000
        int[] validOptions = { 15000, 20000, 25000, 30000, 40000 };
        Assert.Contains(config.BitrateKbps, validOptions);
    }

    // ==================== Zero/Missing Bandwidth ====================

    [Fact]
    public void CalculateSuggestedConfig_ZeroBandwidth_UsesDefault100Mbps()
    {
        var config = StreamingOptimizer.CalculateSuggestedConfig(
            DefaultHardware(), HwEncoder(),
            new SpeedTestResult { PingMs = 10, BandwidthMbps = 0, Success = false });

        // Should still produce valid config (defaults to 100Mbps internally)
        Assert.True(config.BitrateKbps >= 15000);
        Assert.True(config.Monitors >= 1);
    }

    // ==================== Reason String ====================

    [Fact]
    public void CalculateSuggestedConfig_ReasonContainsResolutionAndBitrate()
    {
        var config = StreamingOptimizer.CalculateSuggestedConfig(
            DefaultHardware(), HwEncoder(),
            new SpeedTestResult { PingMs = 5, BandwidthMbps = 500, Success = true });

        Assert.Contains("1440x810", config.Reason);
        Assert.Contains("Mbps", config.Reason);
        Assert.Contains("fps", config.Reason);
    }

    // ==================== Excellent Network Bonus ====================

    [Fact]
    public void CalculateSuggestedConfig_ExcellentNetwork_HigherBitrate()
    {
        var excellentConfig = StreamingOptimizer.CalculateSuggestedConfig(
            DefaultHardware(), HwEncoder(),
            new SpeedTestResult { PingMs = 5, BandwidthMbps = 600, Success = true });

        var averageConfig = StreamingOptimizer.CalculateSuggestedConfig(
            DefaultHardware(), HwEncoder(),
            new SpeedTestResult { PingMs = 30, BandwidthMbps = 100, Success = true });

        // Excellent network should get >= average network bitrate
        Assert.True(excellentConfig.BitrateKbps >= averageConfig.BitrateKbps);
    }
}
