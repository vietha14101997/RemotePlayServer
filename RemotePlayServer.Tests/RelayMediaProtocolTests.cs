using RemotePlayServer.Server;

namespace RemotePlayServer.Tests;

/// <summary>
/// Tests for the DERP-style media-relay envelope. The 1-byte channel tag lets
/// video/audio/cursor/input share one binary WebSocket stream and be demuxed at
/// the far end. Must stay byte-compatible with the client's RelayMediaProtocol.kt.
/// </summary>
public class RelayMediaProtocolTests
{
    [Fact]
    public void Wrap_PrependsChannelTag_AndPreservesPayload()
    {
        var payload = new byte[] { 0x03, 0x01, 0, 1, 0, 5, 9, 9 };
        var wrapped = RelayMediaProtocol.Wrap(RelayMediaProtocol.ChannelVideo, payload);

        Assert.Equal(RelayMediaProtocol.ChannelVideo, wrapped[0]);
        Assert.Equal(payload.Length + 1, wrapped.Length);
        Assert.Equal(payload, wrapped[1..]);
    }

    [Fact]
    public void Wrap_Slice_CopiesOnlyRequestedRange()
    {
        var src = new byte[] { 1, 2, 3, 4, 5 };
        var wrapped = RelayMediaProtocol.Wrap(RelayMediaProtocol.ChannelAudio, src, 1, 3);

        Assert.Equal(RelayMediaProtocol.ChannelAudio, wrapped[0]);
        Assert.Equal(new byte[] { RelayMediaProtocol.ChannelAudio, 2, 3, 4 }, wrapped);
    }

    [Theory]
    [InlineData(0xF1, true)]  // video
    [InlineData(0xF2, true)]  // audio
    [InlineData(0xF3, true)]  // cursor
    [InlineData(0xF5, true)]  // input
    [InlineData(0x03, false)] // inner protocol-v2 type byte — must NOT be an envelope
    [InlineData(0x01, false)] // inner input tag
    public void IsMediaEnvelope_MatchesOnlyChannelTags(byte first, bool expected)
    {
        Assert.Equal(expected, RelayMediaProtocol.IsMediaEnvelope(new byte[] { first, 0x00 }));
    }

    [Fact]
    public void TryUnwrapInput_RoundTrips()
    {
        var input = new byte[] { 0x01, 0xAA, 0xBB };
        var wrapped = RelayMediaProtocol.Wrap(RelayMediaProtocol.ChannelInput, input);

        var unwrapped = RelayMediaProtocol.TryUnwrapInput(wrapped);
        Assert.Equal(input, unwrapped);
    }

    [Fact]
    public void TryUnwrapInput_ReturnsNullForNonInputChannel()
    {
        var video = RelayMediaProtocol.Wrap(RelayMediaProtocol.ChannelVideo, new byte[] { 1, 2 });
        Assert.Null(RelayMediaProtocol.TryUnwrapInput(video));
    }
}
