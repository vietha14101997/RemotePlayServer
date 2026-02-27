using RemotePlayServer.Infrastructure.Network;

namespace RemotePlayServer.Tests;

public class UsbTetheringHelperTests
{
    // ==================== IsUsbTetheringIP ====================

    [Theory]
    [InlineData("192.168.42.1", true)]       // Android default USB tethering
    [InlineData("192.168.42.129", true)]      // Android default range
    [InlineData("192.168.44.1", true)]        // Samsung USB tethering
    [InlineData("192.168.44.200", true)]      // Samsung range
    [InlineData("192.168.137.1", true)]       // Windows ICS
    [InlineData("192.168.137.50", true)]      // Windows ICS range
    public void IsUsbTetheringIP_KnownUsbRanges_ReturnsTrue(string ip, bool expected)
    {
        Assert.Equal(expected, UsbTetheringHelper.IsUsbTetheringIP(ip));
    }

    [Theory]
    [InlineData("192.168.1.1")]             // Normal home LAN
    [InlineData("192.168.0.100")]           // Normal home LAN
    [InlineData("192.168.2.50")]            // Normal home LAN
    [InlineData("10.0.0.1")]               // Corporate LAN
    [InlineData("172.16.0.1")]             // Private class B
    [InlineData("127.0.0.1")]              // Loopback
    [InlineData("8.8.8.8")]                // Public IP
    [InlineData("")]                        // Empty string
    public void IsUsbTetheringIP_NonUsbRanges_ReturnsFalse(string ip)
    {
        Assert.False(UsbTetheringHelper.IsUsbTetheringIP(ip));
    }

    // ==================== Edge Cases ====================

    [Fact]
    public void IsUsbTetheringIP_PrefixOnlyMatch_ReturnsTrue()
    {
        // Ensure prefix matching works correctly (not partial matches)
        Assert.True(UsbTetheringHelper.IsUsbTetheringIP("192.168.42.0"));
        Assert.True(UsbTetheringHelper.IsUsbTetheringIP("192.168.42.255"));
    }

    [Fact]
    public void IsUsbTetheringIP_SimilarButDifferentPrefix_ReturnsFalse()
    {
        // 192.168.4 should not match 192.168.42.
        Assert.False(UsbTetheringHelper.IsUsbTetheringIP("192.168.4.1"));
        // 192.168.43 is not in the known list
        Assert.False(UsbTetheringHelper.IsUsbTetheringIP("192.168.43.1"));
    }
}
