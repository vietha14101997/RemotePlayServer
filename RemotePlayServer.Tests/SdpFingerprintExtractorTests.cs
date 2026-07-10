using RemotePlayServer.Application.Protocol;

namespace RemotePlayServer.Tests;

/// <summary>
/// Unit tests for the pure SDP DTLS-fingerprint helper used by the Phase 5 ICE-restart
/// F11 MITM guard. Logic-only (no SIPSorcery/WebRTC dependency) — sanity-compiled and run
/// cross-platform in a throwaway project before this Windows-only test project could be
/// verified on the target VM (see plans/reports/fullstack-developer-260710-0922-p5-host-ice-restart.md).
/// </summary>
public class SdpFingerprintExtractorTests
{
    private const string OfferSdpWithFingerprint =
        "v=0\r\n" +
        "o=- 123 2 IN IP4 127.0.0.1\r\n" +
        "s=-\r\n" +
        "t=0 0\r\n" +
        "a=group:BUNDLE 0 1\r\n" +
        "m=audio 9 UDP/TLS/RTP/SAVPF 111\r\n" +
        "c=IN IP4 0.0.0.0\r\n" +
        "a=ice-ufrag:abcd\r\n" +
        "a=ice-pwd:efgh12345678901234567890\r\n" +
        "a=fingerprint:sha-256 AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99\r\n" +
        "a=setup:actpass\r\n" +
        "a=mid:0\r\n";

    [Fact]
    public void ExtractDtlsFingerprint_ReturnsValue_WhenPresent()
    {
        var fp = SdpFingerprintExtractor.ExtractDtlsFingerprint(OfferSdpWithFingerprint);

        Assert.Equal("sha-256 AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99", fp);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v=0\r\ns=-\r\nt=0 0\r\nm=audio 9 UDP/TLS/RTP/SAVPF 111\r\n")]
    public void ExtractDtlsFingerprint_ReturnsNull_WhenAbsent(string? sdpWithoutFingerprint)
    {
        Assert.Null(SdpFingerprintExtractor.ExtractDtlsFingerprint(sdpWithoutFingerprint));
    }

    [Fact]
    public void ExtractDtlsFingerprint_HandlesLfOnlyLineEndings()
    {
        var sdp = OfferSdpWithFingerprint.Replace("\r\n", "\n");

        var fp = SdpFingerprintExtractor.ExtractDtlsFingerprint(sdp);

        Assert.Equal("sha-256 AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99", fp);
    }

    [Fact]
    public void FingerprintsMatch_True_ForIdenticalValues()
    {
        var a = "sha-256 AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99";
        var b = "sha-256 AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99";

        Assert.True(SdpFingerprintExtractor.FingerprintsMatch(a, b));
    }

    [Fact]
    public void FingerprintsMatch_True_CaseInsensitive()
    {
        var a = "sha-256 aa:bb:cc:dd:ee:ff:00:11:22:33:44:55:66:77:88:99";
        var b = "SHA-256 AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99";

        Assert.True(SdpFingerprintExtractor.FingerprintsMatch(a, b));
    }

    [Fact]
    public void FingerprintsMatch_False_ForDifferentValues_SimulatedMitm()
    {
        // Simulates F11: a restart offer signed by a DIFFERENT DTLS peer/cert must be rejected.
        var original = "sha-256 AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99";
        var attacker = "sha-256 11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00";

        Assert.False(SdpFingerprintExtractor.FingerprintsMatch(original, attacker));
    }

    [Theory]
    [InlineData(null, "sha-256 AA:BB")]
    [InlineData("sha-256 AA:BB", null)]
    [InlineData(null, null)]
    [InlineData("", "sha-256 AA:BB")]
    public void FingerprintsMatch_False_WhenEitherSideMissing(string? a, string? b)
    {
        // An absent fingerprint must NEVER be treated as "trusted by default".
        Assert.False(SdpFingerprintExtractor.FingerprintsMatch(a, b));
    }
}
