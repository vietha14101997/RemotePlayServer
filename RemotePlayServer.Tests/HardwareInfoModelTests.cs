using RemotePlayServer.Core.Models;

namespace RemotePlayServer.Tests;

public class HardwareInfoModelTests
{
    // ==================== GpuInfo Computed Properties ====================

    [Theory]
    [InlineData(1024, 1.0)]
    [InlineData(2048, 2.0)]
    [InlineData(8192, 8.0)]
    [InlineData(24576, 24.0)]
    [InlineData(512, 0.5)]
    [InlineData(0, 0)]
    public void GpuInfo_VramGB_CalculatedFromVramMB(long vramMB, double expectedGB)
    {
        var gpu = new GpuInfo { VramMB = vramMB };
        Assert.Equal(expectedGB, gpu.VramGB);
    }

    [Theory]
    [InlineData(1024, 1.0)]
    [InlineData(16384, 16.0)]
    [InlineData(32768, 32.0)]
    [InlineData(65536, 64.0)]
    public void RamInfo_TotalGB_CalculatedFromTotalMB(long totalMB, double expectedGB)
    {
        var ram = new RamInfo { TotalMB = totalMB };
        Assert.Equal(expectedGB, ram.TotalGB);
    }

    // ==================== EncoderInfo Defaults ====================

    [Fact]
    public void EncoderInfo_DefaultSupportedCodecs_ContainsH264()
    {
        var encoder = new EncoderInfo();
        Assert.Contains("H264", encoder.SupportedCodecs);
    }

    [Fact]
    public void EncoderInfo_DefaultPreferredCodec_IsH264()
    {
        var encoder = new EncoderInfo();
        Assert.Equal("H264", encoder.PreferredCodec);
    }

    [Fact]
    public void EncoderInfo_DefaultHwAccel_IsFalse()
    {
        var encoder = new EncoderInfo();
        Assert.False(encoder.HwAccel);
    }

    // ==================== MonitorInfoDto ====================

    [Fact]
    public void MonitorInfoDto_DefaultIsVirtual_IsFalse()
    {
        var monitor = new MonitorInfoDto();
        Assert.False(monitor.IsVirtual);
    }

    // ==================== SuggestedConfig Defaults ====================

    [Fact]
    public void SuggestedConfig_Defaults_Reasonable()
    {
        var config = new SuggestedConfig();
        Assert.Equal(3, config.Monitors);
        Assert.Equal(1920, config.ResolutionWidth);
        Assert.Equal(1080, config.ResolutionHeight);
        Assert.Equal(20000, config.BitrateKbps);
        Assert.Equal(60, config.Fps);
    }

    // ==================== CursorType Enum ====================

    [Fact]
    public void CursorType_Arrow_Is1()
    {
        Assert.Equal(1, (int)CursorType.Arrow);
    }

    [Fact]
    public void CursorType_Hand_Is11()
    {
        Assert.Equal(11, (int)CursorType.Hand);
    }

    [Fact]
    public void CursorType_Custom_Is99()
    {
        Assert.Equal(99, (int)CursorType.Custom);
    }
}
