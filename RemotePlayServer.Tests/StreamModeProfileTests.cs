using RemotePlayServer.Configuration;

namespace RemotePlayServer.Tests;

/// <summary>
/// Locks the Gaming/Efficiency mode → runtime-settings mapping. The Gaming assertions are a
/// regression lock: Gaming MUST stay adaptive-OFF at the full 60fps ceiling so a plain build
/// is byte-for-byte identical to the pre-efficiency behaviour.
/// </summary>
public class StreamModeProfileTests
{
    // ==================== Gaming = current defaults (regression lock) ====================

    [Fact]
    public void For_Gaming_AdaptiveDisabled()
    {
        var p = StreamModeProfile.For(StreamMode.Gaming);
        Assert.False(p.AdaptiveFpsEnabled);
    }

    [Fact]
    public void For_Gaming_CeilingIsFull60()
    {
        var p = StreamModeProfile.For(StreamMode.Gaming);
        Assert.Equal(60, p.CeilFps);
        Assert.Equal(StreamModeProfile.GamingCeilFps, p.CeilFps);
    }

    // ==================== Efficiency = adaptive on + lower ceiling ====================

    [Fact]
    public void For_Efficiency_AdaptiveEnabled()
    {
        var p = StreamModeProfile.For(StreamMode.Efficiency);
        Assert.True(p.AdaptiveFpsEnabled);
    }

    [Fact]
    public void For_Efficiency_CeilingCappedAt30()
    {
        var p = StreamModeProfile.For(StreamMode.Efficiency);
        Assert.Equal(30, p.CeilFps);
        Assert.Equal(StreamModeProfile.EfficiencyCeilFps, p.CeilFps);
    }

    [Fact]
    public void For_Efficiency_CeilingBelowGaming()
    {
        var gaming = StreamModeProfile.For(StreamMode.Gaming);
        var efficiency = StreamModeProfile.For(StreamMode.Efficiency);
        Assert.True(efficiency.CeilFps < gaming.CeilFps);
    }

    // ==================== Floor consistent + valid ladder ====================

    [Fact]
    public void For_BothModes_FloorIsDefault20_AndBelowCeil()
    {
        var gaming = StreamModeProfile.For(StreamMode.Gaming);
        var efficiency = StreamModeProfile.For(StreamMode.Efficiency);

        Assert.Equal(20, gaming.FloorFps);
        Assert.Equal(20, efficiency.FloorFps);
        // Floor must be a usable range below each ceiling.
        Assert.True(gaming.FloorFps < gaming.CeilFps);
        Assert.True(efficiency.FloorFps < efficiency.CeilFps);
    }

    [Fact]
    public void For_UnknownMode_DefaultsToGaming()
    {
        // Defensive: any out-of-range enum value falls back to the safe Gaming preset.
        var p = StreamModeProfile.For((StreamMode)999);
        Assert.False(p.AdaptiveFpsEnabled);
        Assert.Equal(60, p.CeilFps);
    }
}
