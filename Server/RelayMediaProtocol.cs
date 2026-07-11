#nullable enable
using System;

namespace RemotePlayServer.Server;

/// <summary>
/// Envelope for DERP-style media-over-relay. When WebRTC ICE fails, encoded
/// media and input share ONE binary WebSocket stream through the Go relay, so a
/// 1-byte channel tag is prepended to each frame to demux at the far end.
/// The tags (0xF1+) stay clear of the inner protocol-v2 type bytes (0x01-0x09).
/// </summary>
public static class RelayMediaProtocol
{
    public const byte ChannelVideo = 0xF1;   // host -> client: [0xF1][v2 video chunk]
    public const byte ChannelAudio = 0xF2;   // host -> client: [0xF2][PCM]
    public const byte ChannelCursor = 0xF3;  // host -> client: [0xF3][cursor bytes]
    public const byte ChannelInput = 0xF5;   // client -> host: [0xF5][input bytes]

    /// <summary>True if this binary WS message is a relay-media envelope (vs. speed-test etc.).</summary>
    public static bool IsMediaEnvelope(byte[] data) =>
        data.Length >= 1 && data[0] >= ChannelVideo && data[0] <= ChannelInput;

    /// <summary>Prepend a channel tag. Allocates a new buffer (payload already framed).</summary>
    public static byte[] Wrap(byte channel, byte[] payload)
    {
        var msg = new byte[payload.Length + 1];
        msg[0] = channel;
        Buffer.BlockCopy(payload, 0, msg, 1, payload.Length);
        return msg;
    }

    /// <summary>Prepend a channel tag onto a slice (avoids a second copy for chunked video).</summary>
    public static byte[] Wrap(byte channel, byte[] payload, int offset, int count)
    {
        var msg = new byte[count + 1];
        msg[0] = channel;
        Buffer.BlockCopy(payload, offset, msg, 1, count);
        return msg;
    }

    /// <summary>If data is an input envelope (0xF5), return its payload; else null.</summary>
    public static byte[]? TryUnwrapInput(byte[] data)
    {
        if (data.Length < 2 || data[0] != ChannelInput) return null;
        var payload = new byte[data.Length - 1];
        Buffer.BlockCopy(data, 1, payload, 0, payload.Length);
        return payload;
    }
}
