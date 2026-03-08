using System;
using System.Collections.Generic;
using RemotePlayServer.Core;

namespace RemotePlayServer.Infrastructure.Network;

/// <summary>
/// Utility to fragment H.265 NAL units into Fragmentation Units (FU) per RFC 7798.
/// This allows sending large H.265 frames over RTP when the underlying stack
/// doesn't support automatic H.265 packetization.
/// </summary>
public static class H265Fragmenter
{
    private const int MAX_RTP_PAYLOAD_SIZE = 1200; // Safe MTU for UDP/WebRTC

    /// <summary>
    /// Prepare an Annex B stream for RTP sending via SIPSorcery.
    ///
    /// CRITICAL: SIPSorcery's SendRtpRaw strips byte[0] of every RTP payload
    /// (it assumes H264's 1-byte NAL header). For H265's 2-byte header, this
    /// breaks the NAL. FU fragmentation also fails because Unity WebRTC's
    /// Encoded Transform only delivers the LAST FU fragment per timestamp.
    ///
    /// SOLUTION: Send each NAL as a single RTP packet with a dummy prefix byte.
    /// SIPSorcery strips the dummy → client receives the complete H265 NAL intact.
    /// Each NAL = one RTP packet with marker=1, so Encoded Transform delivers it.
    /// </summary>
    public static List<byte[]> FragmentAnnexB(byte[] data)
    {
        var nalUs = ParseAnnexB(data);
        var result = new List<byte[]>();

        foreach (var nalU in nalUs)
        {
            if (nalU.Length < 2) continue;

            // Prepend a dummy byte that SIPSorcery will strip.
            // After stripping, the client receives the original NAL with
            // its 2-byte HEVC header intact: [nalType<<1|layerId_hi, layerId_lo<<3|tid, payload...]
            byte[] payload = new byte[1 + nalU.Length];
            payload[0] = 0x00; // dummy — gets stripped by SIPSorcery
            Buffer.BlockCopy(nalU, 0, payload, 1, nalU.Length);
            result.Add(payload);
        }

        return result;
    }

    /// <summary>
    /// Split Annex B stream into individual NAL units (excluding start codes).
    /// </summary>
    private static List<byte[]> ParseAnnexB(byte[] data)
    {
        var result = new List<byte[]>();
        int start = -1;
        int i = 0;

        while (i < data.Length)
        {
            // Check for 4-byte or 3-byte start codes
            int startCodeLen = 0;
            if (i + 4 <= data.Length && data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 0 && data[i + 3] == 1)
                startCodeLen = 4;
            else if (i + 3 <= data.Length && data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
                startCodeLen = 3;

            if (startCodeLen > 0)
            {
                if (start != -1 && start < i)
                {
                    int nalLength = i - start;
                    byte[] nalU = new byte[nalLength];
                    Buffer.BlockCopy(data, start, nalU, 0, nalLength);
                    result.Add(nalU);
                }
                i += startCodeLen;
                start = i;
            }
            else
            {
                i++;
            }
        }

        if (start != -1 && start < data.Length)
        {
            int nalLength = data.Length - start;
            if (nalLength > 0)
            {
                byte[] nalU = new byte[nalLength];
                Buffer.BlockCopy(data, start, nalU, 0, nalLength);
                result.Add(nalU);
            }
        }

        return result;
    }

    /// <summary>
    /// Create Fragmentation Units (FU) for a single large NAL unit.
    /// RFC 7798 Section 4.4.3.
    /// </summary>
    private static List<byte[]> CreateFUs(byte[] nalU)
    {
        if (nalU.Length < 3) return new List<byte[]> { nalU };

        // H.265 NAL Header (2 bytes)
        // F (1), Type (6), LayerId (6), TID (3)
        ushort header = (ushort)((nalU[0] << 8) | nalU[1]);
        int type = (header >> 9) & 0x3F;
        int layerId = (header >> 3) & 0x3F;
        int tid = header & 0x07;

        byte[] payload = nalU.AsSpan(2).ToArray();
        int offset = 0;
        var fragments = new List<byte[]>();

        while (offset < payload.Length)
        {
            int remaining = payload.Length - offset;
            int chunkLen = Math.Min(remaining, MAX_RTP_PAYLOAD_SIZE - 3); // 3 bytes for FU header

            bool isStart = (offset == 0);
            bool isEnd = (offset + chunkLen == payload.Length);

            // FU Payload Header (2 bytes)
            // Type set to 49 (FU)
            byte ph1 = (byte)((49 << 1) | (layerId >> 5));
            byte ph2 = (byte)((layerId << 3) | tid);

            // FU Header (1 byte)
            // S (1), E (1), Type (6)
            byte fuH = (byte)((isStart ? 0x80 : 0) | (isEnd ? 0x40 : 0) | type);

            byte[] fragment = new byte[3 + chunkLen];
            fragment[0] = ph1;
            fragment[1] = ph2;
            fragment[2] = fuH;
            Buffer.BlockCopy(payload, offset, fragment, 3, chunkLen);

            fragments.Add(fragment);
            offset += chunkLen;
        }

        return fragments;
    }
}
