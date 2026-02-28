using RemotePlayServer.Infrastructure.Network;
using RemotePlayServer.Tests.Helpers;

namespace RemotePlayServer.Tests;

public class SpeedTestClassificationTests
{
    /// <summary>
    /// Test the private ClassifyConnection method via reflection.
    /// </summary>
    private static string ClassifyConnection(double pingMs, double mbps)
    {
        return (string)ReflectionHelper.InvokePrivateStaticMethod(
            typeof(SpeedTest), "ClassifyConnection", pingMs, mbps)!;
    }

    [Fact]
    public void ClassifyConnection_LowPingHighBandwidth_LAN()
    {
        // ping < 5ms AND bandwidth > 500Mbps → LAN
        Assert.Equal("LAN", ClassifyConnection(2, 900));
        Assert.Equal("LAN", ClassifyConnection(4.9, 501));
    }

    [Fact]
    public void ClassifyConnection_MediumPingGoodBandwidth_WiFi()
    {
        // ping < 20ms AND bandwidth > 100Mbps → WiFi
        Assert.Equal("WiFi", ClassifyConnection(10, 200));
        Assert.Equal("WiFi", ClassifyConnection(19, 101));
    }

    [Fact]
    public void ClassifyConnection_HighPing_Internet()
    {
        // ping >= 20ms → Internet
        Assert.Equal("Internet", ClassifyConnection(25, 500));
        Assert.Equal("Internet", ClassifyConnection(100, 50));
    }

    [Fact]
    public void ClassifyConnection_LowBandwidth_NotLAN()
    {
        // Low ping but low bandwidth → not LAN (needs > 500Mbps)
        // ping < 20 && bandwidth > 100 → WiFi
        var result = ClassifyConnection(3, 150);
        Assert.Equal("WiFi", result);
    }

    [Fact]
    public void ClassifyConnection_VeryLowBandwidth_Internet()
    {
        // bandwidth <= 100 → Internet (regardless of ping)
        Assert.Equal("Internet", ClassifyConnection(25, 50));
    }
}
