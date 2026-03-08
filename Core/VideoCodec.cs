#nullable enable

namespace RemotePlayServer.Core;

/// <summary>
/// GPU Vendor enumeration for encoder selection
/// </summary>
public enum GpuVendorType { Unknown, NVIDIA, AMD, Intel }

/// <summary>
/// Video codec enumeration for encoder selection
/// </summary>
public enum VideoCodec
{
    H264,   // AVC - Hardware accelerated (NVENC/AMF/QSV)
    H265,   // HEVC - Better compression (30-50% more efficient)
    VP9,    // libvpx-vp9 software encoder (fallback)
    VP8     // libvpx software encoder (final fallback)
}
