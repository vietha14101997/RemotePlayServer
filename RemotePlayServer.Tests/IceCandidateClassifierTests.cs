using RemotePlayServer.Server;

namespace RemotePlayServer.Tests;

/// <summary>
/// Tests for the pure contract-v1 candidate classifier (host/srflx/prflx/relay,
/// ipv4/ipv6, udp/tcp, relay_protocol). Covers both wire formats: SDP lines
/// ("a=candidate:..." / "candidate:...") and SIPSorcery's bare RTCIceCandidate.candidate form.
/// </summary>
public class IceCandidateClassifierTests
{
    [Theory]
    [InlineData("candidate:1 1 udp 2122194687 192.168.1.10 50000 typ host generation 0", "host", "ipv4", "udp", "none")]
    [InlineData("candidate:2 1 tcp 1518214911 192.168.1.10 50001 typ host tcptype active", "host", "ipv4", "tcp", "none")]
    [InlineData("candidate:3 1 udp 1685 203.0.113.5 50000 typ srflx raddr 192.168.1.10 rport 50000", "srflx", "ipv4", "udp", "none")]
    [InlineData("candidate:4 1 udp 41885439 203.0.113.9 3478 typ relay raddr 0.0.0.0 rport 0", "relay", "ipv4", "udp", "udp")]
    [InlineData("candidate:5 1 tcp 41885439 203.0.113.9 3478 typ relay raddr 0.0.0.0 rport 0", "relay", "ipv4", "tcp", "tcp")]
    [InlineData("candidate:6 1 udp 2122 2001:db8::5 50000 typ host generation 0", "host", "ipv6", "udp", "none")]
    // SIPSorcery bare form (no "candidate:" prefix)
    [InlineData("869 1 udp 2122194687 10.0.0.5 50000 typ host generation 0", "host", "ipv4", "udp", "none")]
    [InlineData("870 1 udp 41885439 203.0.113.9 3478 typ relay raddr 0.0.0.0 rport 0", "relay", "ipv4", "udp", "udp")]
    public void Classify_KnownCandidateLines_ReturnsExpectedEnums(
        string candidate, string expectedType, string expectedFamily, string expectedProtocol, string expectedRelayProtocol)
    {
        var result = IceCandidateClassifier.Classify(candidate);

        Assert.Equal(expectedType, result.CandidateType);
        Assert.Equal(expectedFamily, result.AddressFamily);
        Assert.Equal(expectedProtocol, result.Protocol);
        Assert.Equal(expectedRelayProtocol, result.RelayProtocol);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("end-of-candidates")]
    [InlineData("candidate:9 1 udp")] // too short to contain "typ <type>"
    public void Classify_MalformedOrControlStrings_FoldsToUnknown(string? candidate)
    {
        var result = IceCandidateClassifier.Classify(candidate);

        Assert.Equal("unknown", result.CandidateType);
        Assert.Equal("unknown", result.AddressFamily);
        Assert.Equal("unknown", result.Protocol);
        Assert.Equal("unknown", result.RelayProtocol);
    }

    [Fact]
    public void Classify_UnparseableAddress_FoldsAddressFamilyToUnknown()
    {
        // mDNS hostname candidate — valid ICE, but not a literal IP.
        var result = IceCandidateClassifier.Classify("candidate:7 1 udp 2122 4f3c9e.local 50000 typ host");

        Assert.Equal("host", result.CandidateType);
        Assert.Equal("unknown", result.AddressFamily);
        Assert.Equal("udp", result.Protocol);
        Assert.Equal("none", result.RelayProtocol);
    }
}
