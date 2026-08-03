#nullable enable

namespace RemotePlayServer.Infrastructure.Encoding;

/// <summary>
/// Server-side NAL-unit keyframe detector for H.264 / HEVC.
///
/// <para><b>Why this exists</b>: the native AMF/NVENC/QSV wrappers in
/// <c>Native/AmfWrapper/NalUtils.h</c> only match 4-byte Annex-B start codes
/// (<c>00 00 00 01</c>). AMF HEVC occasionally emits 3-byte start codes
/// (<c>00 00 01</c>) for IDR frames — those are silently mis-classified as P-frames,
/// and the streamer never tags them as keyframes in the wire protocol. The client
/// decoder then sees P-frames referencing the OLD keyframe and reconstructs
/// "old background + new content delta" until the next genuine IDR.</para>
///
/// <para>This detector runs in the managed C# callback path and is the source of
/// truth for the <c>isKeyFrame</c> flag passed to the streamer. The native
/// flag is treated as a hint only — if the native flag says "keyframe" we trust
/// it; if it says "P-frame" we double-check with NAL parsing and override if
/// we find an IDR/VPS/SPS/CRA NAL unit.</para>
///
/// <para>Both 3-byte (<c>00 00 01</c>) and 4-byte (<c>00 00 00 01</c>) start
/// codes are matched, matching the H.264/HEVC Annex-B spec.</para>
/// </summary>
public static class KeyframeDetector
{
    /// <summary>
    /// Detect whether the encoded chunk contains any H.264 or HEVC keyframe
    /// NAL unit. Always prefers accuracy over speed: scans the whole buffer
    /// (~200-450 KB worst case) because IDR NALs can appear at any position
    /// in the access unit.
    /// </summary>
    /// <param name="data">Encoded access unit (may contain param sets + slice NALs).</param>
    /// <param name="length">Number of valid bytes in <paramref name="data"/>.</param>
    /// <param name="isHevc">True for HEVC, false for H.264.</param>
    /// <returns>True if a keyframe NAL (IDR / VPS / SPS / CRA) is found.</returns>
    public static unsafe bool IsKeyframe(byte* data, int length, bool isHevc)
    {
        if (data == null || length < 4) return false;

        if (isHevc)
        {
            // HEVC NAL unit header is 2 bytes. The 6-bit nal_unit_type field
            // occupies bits 1..6 of the FIRST byte (the LSB is forbidden_zero_bit).
            //   nalType = (byte0 >> 1) & 0x3F
            // Key types: VPS=32, SPS=33, IDR_W_RADL=19, IDR_N_LP=20, CRA=21.
            // We treat VPS+SPS as keyframes too because they appear at the start
            // of an IDR access unit and we want any such frame to be tagged as a
            // keyframe for the wire protocol.
            for (int i = 0; i + 2 < length; i++)
            {
                if (data[i] == 0 && data[i + 1] == 0)
                {
                    // 4-byte start code: 00 00 00 01
                    if (i + 3 < length && data[i + 2] == 0 && data[i + 3] == 1)
                    {
                        int nalType = (data[i + 4] >> 1) & 0x3F;
                        if (IsHevcKeyframeNalType(nalType)) return true;
                        i += 3; // skip past start code on next iteration
                        continue;
                    }
                    // 3-byte start code: 00 00 01
                    if (data[i + 2] == 1)
                    {
                        if (i + 3 >= length) break;
                        int nalType = (data[i + 3] >> 1) & 0x3F;
                        if (IsHevcKeyframeNalType(nalType)) return true;
                        i += 2;
                        continue;
                    }
                }
            }
        }
        else
        {
            // H.264 NAL unit header is 1 byte. The 5-bit nal_unit_type field
            // occupies bits 0..4 (mask 0x1F).
            //   nalType = byte0 & 0x1F
            // Key types: SPS=7, IDR=5. We also accept IDR variants (type 5).
            for (int i = 0; i + 2 < length; i++)
            {
                if (data[i] == 0 && data[i + 1] == 0)
                {
                    // 4-byte start code
                    if (i + 3 < length && data[i + 2] == 0 && data[i + 3] == 1)
                    {
                        if (i + 4 >= length) break;
                        int nalType = data[i + 4] & 0x1F;
                        if (IsH264KeyframeNalType(nalType)) return true;
                        i += 3;
                        continue;
                    }
                    // 3-byte start code
                    if (data[i + 2] == 1)
                    {
                        if (i + 3 >= length) break;
                        int nalType = data[i + 3] & 0x1F;
                        if (IsH264KeyframeNalType(nalType)) return true;
                        i += 2;
                        continue;
                    }
                }
            }
        }

        return false;
    }

    private static bool IsHevcKeyframeNalType(int nalType) =>
        nalType == 32 /*VPS*/ ||
        nalType == 33 /*SPS*/ ||
        nalType == 19 /*IDR_W_RADL*/ ||
        nalType == 20 /*IDR_N_LP*/ ||
        nalType == 21 /*CRA*/;

    private static bool IsH264KeyframeNalType(int nalType) =>
        nalType == 7 /*SPS*/ ||
        nalType == 5 /*IDR*/;
}
