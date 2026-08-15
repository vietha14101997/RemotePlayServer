using RemotePlayServer.Server;

namespace RemotePlayServer.Tests;

/// <summary>
/// Tests for the routability filter applied to locally-gathered ICE candidates
/// before they are sent to the client (initial answer + trickle path).
/// Covers both wire formats: SDP lines ("a=candidate:..." / "candidate:...")
/// and SIPSorcery's RTCIceCandidate.candidate bare form (no "candidate:" prefix).
/// </summary>
public class IceCandidateInspectorTests
{
    [Theory]
    // SDP form — unroutable addresses must be dropped
    [InlineData("candidate:1 1 udp 2122194687 127.0.0.1 50000 typ host generation 0")]
    [InlineData("candidate:2 1 udp 2122194687 ::1 50000 typ host")]
    [InlineData("candidate:3 1 udp 2122 fe80::abcd:1234 50000 typ host")]
    [InlineData("candidate:4 1 udp 2122 0.0.0.0 50000 typ host")]
    [InlineData("a=candidate:5 1 udp 2122 ::1 50000 typ host")]
    // SIPSorcery bare form (RTCIceCandidate.candidate has no "candidate:" prefix)
    [InlineData("869 1 udp 2122194687 127.0.0.1 50000 typ host generation 0")]
    [InlineData("870 1 udp 2122194687 ::1 50000 typ host generation 0")]
    [InlineData("871 1 udp 2122 fe80::1%12 50001 typ host generation 0")]
    public void UnroutableCandidates_AreRejected(string candidate)
    {
        Assert.False(IceCandidateInspector.IsRoutable(candidate));
    }

    [Theory]
    // SDP form — reachable addresses and non-IP hostnames must pass
    [InlineData("candidate:5 1 udp 2122 192.168.1.10 50000 typ host")]
    [InlineData("candidate:6 1 udp 1685 203.0.113.5 50000 typ srflx raddr 192.168.1.10 rport 50000")]
    [InlineData("candidate:7 1 udp 41885439 203.0.113.9 3478 typ relay raddr 0.0.0.0 rport 0")]
    [InlineData("candidate:8 1 udp 2122 4f3c9e.local 50000 typ host")]
    // SIPSorcery bare form
    [InlineData("872 1 udp 2122 192.168.1.10 50000 typ host generation 0")]
    [InlineData("873 1 udp 2122 2001:db8::5 50000 typ host generation 0")]
    [InlineData("874 1 udp 2122 169.254.10.20 50000 typ host generation 0")] // APIPA kept: works on same L2
    public void RoutableCandidates_AreKept(string candidate)
    {
        Assert.True(IceCandidateInspector.IsRoutable(candidate));
    }

    [Theory]
    // Control strings and malformed input must pass through untouched (fail-open)
    [InlineData("end-of-candidates")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("candidate:9 1 udp")]
    public void NonCandidateStrings_PassThrough(string? value)
    {
        Assert.True(IceCandidateInspector.IsRoutable(value));
    }

    [Theory]
    [InlineData("candidate:1 1 udp 2122 192.168.1.10 50000 typ host", true)]
    [InlineData("candidate:2 1 udp 2122 10.0.0.5 50000 typ host", true)]
    [InlineData("candidate:3 1 udp 2122 172.20.1.5 50000 typ host", true)]
    [InlineData("candidate:4 1 udp 2122 100.90.7.13 50000 typ host", true)]
    [InlineData("candidate:5 1 udp 2122 6.217.208.144 50000 typ host", true)]
    [InlineData("candidate:6 1 udp 2122 127.0.0.1 50000 typ host", true)]
    [InlineData("candidate:7 1 udp 2122 ::1 50000 typ host", true)]
    [InlineData("candidate:8 1 udp 2122 fe80::1 50000 typ host", true)]
    [InlineData("candidate:9 1 udp 1685 42.119.222.65 50000 typ srflx raddr 192.168.1.10 rport 50000", false)]
    [InlineData("candidate:10 1 udp 41885439 160.30.157.234 3478 typ relay raddr 0.0.0.0 rport 0", false)]
    [InlineData("candidate:11 1 udp 2122 203.0.113.5 50000 typ host", false)]
    [InlineData("872 1 udp 2122 192.168.1.10 50000 typ host generation 0", true)]
    [InlineData("873 1 udp 41885439 160.30.157.234 3478 typ relay generation 0", false)]
    public void IsPrivateOrLoopbackHostCandidate_ClassifiesCorrectly(string candidate, bool expected)
    {
        Assert.Equal(expected, IceCandidateInspector.IsPrivateOrLoopbackHostCandidate(candidate));
    }
}
