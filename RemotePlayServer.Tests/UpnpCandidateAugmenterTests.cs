using System.Net;
using RemotePlayServer.Application.Protocol;
using RemotePlayServer.Infrastructure.Network.Upnp;
using RemotePlayServer.Server;

namespace RemotePlayServer.Tests;

/// <summary>
/// Tests for the UPnP candidate augmenter: parsing LAN host candidates (both SDP
/// and SIPSorcery bare wire formats) and synthesizing the public srflx candidate
/// advertised after a router port mapping. Live SSDP/SOAP behavior was verified
/// against a real IGD (WANIPConnection:1) in a cross-platform scratch run.
/// </summary>
public class UpnpCandidateAugmenterTests
{
    [Theory]
    [InlineData("candidate:1 1 udp 2122 192.168.1.211 65238 typ host generation 0", "192.168.1.211", 65238)]
    [InlineData("a=candidate:1 1 udp 2122 10.0.0.7 50000 typ host", "10.0.0.7", 50000)]
    [InlineData("2759524381 1 udp 2113937663 172.16.5.9 57296 typ host generation 0", "172.16.5.9", 57296)] // bare SIPSorcery form
    public void PrivateUdpHostCandidates_AreParsed(string candidate, string expectedIp, int expectedPort)
    {
        Assert.True(UpnpCandidateAugmenter.TryParsePrivateUdpHostCandidate(candidate, out var ip, out var port));
        Assert.Equal(expectedIp, ip);
        Assert.Equal(expectedPort, port);
    }

    [Theory]
    [InlineData("candidate:6 1 udp 1685 203.0.113.5 50000 typ srflx raddr 192.168.1.10 rport 50000")] // srflx
    [InlineData("candidate:6 1 udp 2122 113.23.54.74 50000 typ host")]                                 // public host
    [InlineData("candidate:6 1 tcp 1518 192.168.1.10 50000 typ host tcptype passive")]                 // tcp
    [InlineData("candidate:6 2 udp 2122 192.168.1.10 50001 typ host")]                                 // component 2
    [InlineData("candidate:6 1 udp 2122 2405:4802::1 50000 typ host")]                                 // ipv6
    [InlineData("candidate:6 1 udp 2122 4f3c9e.local 50000 typ host")]                                 // mDNS
    [InlineData("end-of-candidates")]
    [InlineData(null)]
    public void NonMappableCandidates_AreRejected(string? candidate)
    {
        Assert.False(UpnpCandidateAugmenter.TryParsePrivateUdpHostCandidate(candidate, out _, out _));
    }

    [Fact]
    public void SynthesizedSrflxCandidate_HasExpectedShapeAndPassesInspector()
    {
        var candidate = UpnpCandidateAugmenter.BuildSrflxCandidate(
            IPAddress.Parse("113.23.54.74"), 57296, "192.168.1.211", 57296);

        Assert.Equal(
            "candidate:upnp57296 1 udp 1694498559 113.23.54.74 57296 typ srflx raddr 192.168.1.211 rport 57296 generation 0",
            candidate);
        Assert.True(IceCandidateInspector.IsRoutable(candidate));
        // Must not be re-detected as a mappable host candidate (no augment loop)
        Assert.False(UpnpCandidateAugmenter.TryParsePrivateUdpHostCandidate(candidate, out _, out _));
    }

    [Theory]
    [InlineData("113.23.54.74", true)]
    [InlineData("100.72.1.5", false)]   // CGNAT 100.64/10
    [InlineData("100.63.255.255", true)] // just below CGNAT range
    [InlineData("192.168.1.1", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("172.31.0.1", false)]
    [InlineData("169.254.10.20", false)]
    [InlineData("127.0.0.1", false)]
    public void IsPublicV4_ClassifiesRanges(string ip, bool expected)
    {
        Assert.Equal(expected, UpnpPortMappingService.IsPublicV4(IPAddress.Parse(ip)));
    }
}
