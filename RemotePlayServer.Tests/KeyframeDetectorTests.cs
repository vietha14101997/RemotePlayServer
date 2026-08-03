using RemotePlayServer.Infrastructure.Encoding;

namespace RemotePlayServer.Tests;

/// <summary>
/// Tests for the managed-side H.264/HEVC NAL-unit keyframe detector.
/// </summary>
public class KeyframeDetectorTests
{
    /// <summary>
    /// Build a buffer with start code + NAL type bytes. Returns null-terminated
    /// sequence of NAL units (some keyframe, some not) so the detector must
    /// scan the whole buffer to find the right one.
    /// </summary>
    private static byte[] BuildHevc(params (int startCodeLen, int nalType)[] nals)
    {
        var ms = new System.IO.MemoryStream();
        foreach (var (scLen, nalType) in nals)
        {
            // HEVC NAL header is 2 bytes. nalType occupies bits 1..6 of byte 0:
            //   byte0 = (nalType << 1) | forbidden_zero_bit
            //   byte1 = nuh_temporal_id_plus1 (use 1 = no temporal sub-layer)
            byte byte0 = (byte)((nalType << 1) & 0x7E);
            byte byte1 = 0x01;
            // Padding "slice" bytes so the parser has something after the NAL header.
            byte[] slice = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };

            if (scLen == 3) ms.Write(new byte[] { 0x00, 0x00, 0x01 }, 0, 3);
            else if (scLen == 4) ms.Write(new byte[] { 0x00, 0x00, 0x00, 0x01 }, 0, 4);
            else throw new ArgumentException("startCodeLen must be 3 or 4");

            ms.WriteByte(byte0);
            ms.WriteByte(byte1);
            ms.Write(slice, 0, slice.Length);
        }
        return ms.ToArray();
    }

    private static byte[] BuildH264(params (int startCodeLen, int nalType)[] nals)
    {
        var ms = new System.IO.MemoryStream();
        foreach (var (scLen, nalType) in nals)
        {
            // H.264 NAL header is 1 byte. nalType occupies bits 0..4:
            //   byte0 = nalType & 0x1F
            byte byte0 = (byte)(nalType & 0x1F);
            byte[] slice = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };

            if (scLen == 3) ms.Write(new byte[] { 0x00, 0x00, 0x01 }, 0, 3);
            else if (scLen == 4) ms.Write(new byte[] { 0x00, 0x00, 0x00, 0x01 }, 0, 4);
            else throw new ArgumentException("startCodeLen must be 3 or 4");

            ms.WriteByte(byte0);
            ms.Write(slice, 0, slice.Length);
        }
        return ms.ToArray();
    }

    private static unsafe bool RunDetector(byte[] data, bool isHevc)
    {
        fixed (byte* p = data)
        {
            return KeyframeDetector.IsKeyframe(p, data.Length, isHevc);
        }
    }

    // ========== HEVC ==========

    [Fact]
    public void Hevc_IDR_W_RADL_4ByteStartCode_IsKeyframe()
    {
        var data = BuildHevc((4, 19)); // IDR_W_RADL
        Assert.True(RunDetector(data, isHevc: true));
    }

    [Fact]
    public void Hevc_IDR_N_LP_4ByteStartCode_IsKeyframe()
    {
        var data = BuildHevc((4, 20)); // IDR_N_LP
        Assert.True(RunDetector(data, isHevc: true));
    }

    [Fact]
    public void Hevc_CRA_4ByteStartCode_IsKeyframe()
    {
        var data = BuildHevc((4, 21)); // CRA
        Assert.True(RunDetector(data, isHevc: true));
    }

    [Fact]
    public void Hevc_VPS_SPS_4ByteStartCode_IsKeyframe()
    {
        // VPS=32, SPS=33 — included so a stream starting with param sets is detected.
        var data = BuildHevc((4, 32), (4, 33));
        Assert.True(RunDetector(data, isHevc: true));
    }

    /// <summary>
    /// THE BUG FIX: the native parser in NalUtils.h only matches 4-byte start codes,
    /// so it misses IDR frames emitted with the spec-legal 3-byte form. The detector
    /// must accept both.
    /// </summary>
    [Fact]
    public void Hevc_IDR_3ByteStartCode_IsKeyframe_RegressionTest()
    {
        var data = BuildHevc((3, 19)); // IDR_W_RADL with 3-byte start code
        Assert.True(RunDetector(data, isHevc: true));
    }

    [Fact]
    public void Hevc_IDR_3ByteStartCode_AfterPNal_IsKeyframe()
    {
        // Real-world case: AMF may emit param sets with 4-byte start code, then an IDR with 3-byte.
        var data = BuildHevc((4, 32), (4, 33), (3, 19));
        Assert.True(RunDetector(data, isHevc: true));
    }

    [Fact]
    public void Hevc_PFrameOnly_NotKeyframe()
    {
        var data = BuildHevc((4, 1)); // TRAIL_N (non-keyframe)
        Assert.False(RunDetector(data, isHevc: true));
    }

    [Fact]
    public void Hevc_EmptyOrTiny_NotKeyframe()
    {
        Assert.False(RunDetector(new byte[] { 0x00, 0x00, 0x01 }, isHevc: true));
        Assert.False(RunDetector(Array.Empty<byte>(), isHevc: true));
    }

    // ========== H.264 ==========

    [Fact]
    public void H264_IDR_4ByteStartCode_IsKeyframe()
    {
        var data = BuildH264((4, 5)); // IDR
        Assert.True(RunDetector(data, isHevc: false));
    }

    [Fact]
    public void H264_SPS_4ByteStartCode_IsKeyframe()
    {
        var data = BuildH264((4, 7)); // SPS
        Assert.True(RunDetector(data, isHevc: false));
    }

    [Fact]
    public void H264_IDR_3ByteStartCode_IsKeyframe()
    {
        var data = BuildH264((3, 5));
        Assert.True(RunDetector(data, isHevc: false));
    }

    [Fact]
    public void H264_NonIDR_NotKeyframe()
    {
        var data = BuildH264((4, 1)); // non-IDR slice
        Assert.False(RunDetector(data, isHevc: false));
    }
}
