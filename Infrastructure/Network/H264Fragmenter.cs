using System;
using System.Collections.Generic;

namespace RemotePlayServer.Infrastructure.Network;

/// <summary>
/// Utility to fragment H.264 NAL units into Fragmentation Units (FU-A) per RFC 6184.
/// This allows sending large H.264 frames over RTP when the underlying stack
/// doesn't support automatic H.264 packetization.
/// </summary>
public static class H264Fragmenter
{
    private const int MAX_RTP_PAYLOAD_SIZE = 1200; // Safe MTU for UDP/WebRTC

    /// <summary>
    /// Fragment an Annex B stream (multiple NALUs) into a list of RTP-ready payloads.
    /// If a NALU is small, it stays as is. If large, it's converted to multiple FUs.
    /// </summary>
    public static List<byte[]> FragmentAnnexB(byte[] data)
    {
        var nalUs = ParseAnnexB(data);
        var result = new List<byte[]>();

        foreach (var nalU in nalUs)
        {
            if (nalU.Length <= MAX_RTP_PAYLOAD_SIZE)
            {
                result.Add(nalU);
            }
            else
            {
                result.AddRange(CreateFUs(nalU));
            }
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
            // Check for 3-byte or 4-byte start codes
            int startCodeLen = 0;
            if (i + 3 < data.Length && data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 0 && data[i + 3] == 1)
                startCodeLen = 4;
            else if (i + 2 < data.Length && data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
                startCodeLen = 3;

            if (startCodeLen > 0)
            {
                if (start != -1)
                {
                    byte[] nalU = new byte[i - start];
                    Buffer.BlockCopy(data, start, nalU, 0, nalU.Length);
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
            byte[] nalU = new byte[data.Length - start];
            Buffer.BlockCopy(data, start, nalU, 0, nalU.Length);
            result.Add(nalU);
        }

        return result;
    }

    /// <summary>
    /// Create FU-A fragmentation units for a single large H.264 NAL unit.
    /// RFC 6184 Section 5.8.
    /// </summary>
    private static List<byte[]> CreateFUs(byte[] nalU)
    {
        if (nalU.Length < 1) return new List<byte[]> { nalU };

        // H.264 NAL Header (1 byte): F (1), NRI (2), Type (5)
        byte nalHeader = nalU[0];
        int type = nalHeader & 0x1F;
        int nri = (nalHeader >> 5) & 0x03;
        int f = (nalHeader >> 7) & 0x01;

        // Strip the NAL header from the secondary payload
        byte[] payload = nalU.AsSpan(1).ToArray();
        int offset = 0;
        var fragments = new List<byte[]>();

        while (offset < payload.Length)
        {
            int remaining = payload.Length - offset;
            int chunkLen = Math.Min(remaining, MAX_RTP_PAYLOAD_SIZE - 2); // 2 bytes for FU-A header

            bool isStart = (offset == 0);
            bool isEnd = (offset + chunkLen == payload.Length);

            // FU indicator: F(1) | NRI(2) | Type(5) where Type=28 for FU-A
            byte fuIndicator = (byte)((f << 7) | (nri << 5) | 28);
            
            // FU header: S(1) | E(1) | R(1) | Type(5)
            // S=Start, E=End, R=Reserved(0), Type=Original NAL Type
            byte fuHeader = (byte)((isStart ? 0x80 : 0) | (isEnd ? 0x40 : 0) | type);

            byte[] fragment = new byte[2 + chunkLen];
            fragment[0] = fuIndicator;
            fragment[1] = fuHeader;
            Buffer.BlockCopy(payload, offset, fragment, 2, chunkLen);

            fragments.Add(fragment);
            offset += chunkLen;
        }

        return fragments;
    }
}
