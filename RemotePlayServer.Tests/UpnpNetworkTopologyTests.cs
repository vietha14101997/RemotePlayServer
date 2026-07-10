using System.Net;
using RemotePlayServer.Infrastructure.Network.Upnp;

namespace RemotePlayServer.Tests;

/// <summary>
/// Tests for the UPnP topology helpers: same-subnet NIC selection (multi-NIC machines
/// gather host candidates on virtual adapters the router cannot route back to) and the
/// SSRF guard that only lets the host fetch device descriptions from LAN addresses.
/// </summary>
public class UpnpNetworkTopologyTests
{
    [Theory]
    [InlineData("192.168.1.211", "192.168.1.1", "255.255.255.0", true)]
    [InlineData("192.168.56.1", "192.168.1.1", "255.255.255.0", false)]  // VirtualBox adapter vs real LAN
    [InlineData("10.0.5.20", "10.0.5.1", "255.255.255.0", true)]
    [InlineData("10.0.5.20", "10.0.6.1", "255.255.255.0", false)]
    [InlineData("172.20.10.2", "172.20.10.1", "255.255.255.240", true)]
    public void SameNetwork_ComparesMaskedBits(string local, string gateway, string mask, bool expected)
    {
        Assert.Equal(expected, UpnpNetworkTopology.SameNetwork(
            IPAddress.Parse(local), IPAddress.Parse(gateway), IPAddress.Parse(mask)));
    }

    [Theory]
    [InlineData("http://192.168.1.1:5000/desc.xml", true)]
    [InlineData("http://10.0.0.1/root.xml", true)]
    [InlineData("http://172.16.0.1/d.xml", true)]
    [InlineData("http://169.254.1.1/d.xml", true)]      // link-local
    [InlineData("http://127.0.0.1:8080/d.xml", true)]   // loopback (test setups)
    [InlineData("http://8.8.8.8/evil.xml", false)]      // public IP — SSRF attempt
    [InlineData("http://203.0.113.9/x.xml", false)]
    [InlineData("http://evil.example.com/x.xml", false)] // hostname, not IP literal
    public void IsTrustedLanUrl_OnlyAllowsPrivateLiterals(string url, bool expected)
    {
        Assert.Equal(expected, UpnpNetworkTopology.IsTrustedLanUrl(new System.Uri(url)));
    }
}
