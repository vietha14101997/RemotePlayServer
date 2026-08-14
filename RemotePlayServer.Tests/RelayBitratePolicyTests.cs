using RemotePlayServer.Application.Streaming;

namespace RemotePlayServer.Tests;

public class RelayBitratePolicyTests
{
    [Fact]
    public void RelayPolicy_RestoresPreRelayTargetAcrossRepeatedTransitions()
    {
        using var streamer = new SIPSorceryStreamer(1, 60, 1080);
        streamer.ForceSetBitrate(12000);

        streamer.ApplyRelayBitratePolicy();
        Assert.Equal(6000, streamer.GetCurrentConfig().TotalBitrateKbps);

        // Re-entering while already relaying must not overwrite the original target.
        streamer.ApplyRelayBitratePolicy();
        streamer.RestorePreRelayBitratePolicy();
        Assert.Equal(12000, streamer.GetCurrentConfig().TotalBitrateKbps);

        streamer.ForceSetBitrate(10000);
        streamer.ApplyRelayBitratePolicy();
        streamer.RestorePreRelayBitratePolicy();
        Assert.Equal(10000, streamer.GetCurrentConfig().TotalBitrateKbps);
    }
}
